using System.Diagnostics;
using System.Security.Cryptography;
using HaruyasumiRyokouki.DataTool.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

/// <summary>
/// review --ui: the review as a page in the browser instead of a YAML file in an editor. A small
/// web server on 127.0.0.1 shows the file that waits (vocabulary.yaml, then translations.yaml),
/// the problems and the photos of every tag; edits are checked as they are made and written to
/// the same file, and the approval is the same as review --approve. The YAML stays the record of
/// what was approved. The server stops after the approval or on Ctrl+C.
/// </summary>
internal sealed class ReviewServer(Workspace workspace, SqliteConnection connection, TextWriter output)
{
	/// <summary>Photos the browser can show as they are; others (HEIC, TIFF) are listed by name.</summary>
	private static readonly Dictionary<string, string> BrowserImages = new(StringComparer.OrdinalIgnoreCase)
	{
		[".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp",
		[".gif"] = "image/gif", [".bmp"] = "image/bmp", [".avif"] = "image/avif"
	};

	/// <summary>"on line 12" or "in the tag on line 12" in a message of the checks.</summary>
	private static readonly System.Text.RegularExpressions.Regex LineReference = new(@"(on|in the tag on) line (\d+)");

	private readonly ReviewStep _review =new(workspace.VocabularyPath, workspace.TranslationsPath, TextWriter.Null);
	private readonly object _lock = new();

	/// <summary>The model of the page: the vocabulary or the translations, as edited.</summary>
	public sealed class Model
	{
		public List<VocabularyFile.Entry>? Tags { get; set; }
		public List<VocabularyFile.DroppedEntry>? Dropped { get; set; }
		public List<TranslationsFile.Entry>? Translations { get; set; }
	}

	/// <summary>A problem for the page, linked to the tag it is about by its position.</summary>
	/// <param name="Other">The position of the other tag of a problem between two tags (the same label), for a merge.</param>
	public sealed record PageIssue(int? Tag, string? Slug, string Message, bool IsError, int? Other);

	/// <summary>A change of the approved vocabulary made on the translations page.</summary>
	/// <param name="Action"><c>delete</c> or <c>merge</c>.</param>
	/// <param name="Into">For a merge, the slug of the tag that takes the other one in.</param>
	/// <param name="Translations">The translations as edited on the page, unsaved edits included.</param>
	public sealed record VocabularyChange(string Action, string Slug, string? Into, List<TranslationsFile.Entry> Translations);

	/// <summary>Returns the exit code: 0 after an approval, 1 when stopped without one.</summary>
	public async Task<int> RunAsync(bool openBrowser, CancellationToken cancellationToken)
	{
		_review.Current(connection);   // fails early with a clear message when there is nothing to review

		var builder = WebApplication.CreateSlimBuilder();
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		var app = builder.Build();

		// Another page open in the same browser must not be able to call the API.
		string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
		app.Use(async (context, next) =>
		{
			if (context.Request.Path.StartsWithSegments("/api") && context.Request.Query["token"] != token)
			{
				context.Response.StatusCode = StatusCodes.Status403Forbidden;
				return;
			}
			await next();
		});

		var approved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		app.MapGet("/", () => Results.Content(Page().Replace("{{token}}", token), "text/html; charset=utf-8"));
		app.MapGet("/api/state", () => Locked(State));
		app.MapPost("/api/check", (Model model) => Locked(() => Check(model)));
		app.MapPost("/api/save", (Model model) => Locked(() => Save(model)));
		app.MapPost("/api/approve", (Model model) => Locked(() =>
		{
			var result = Approve(model);
			if (result.Ok)
				approved.TrySetResult(true);
			return (object)result;
		}));
		app.MapPost("/api/vocabulary", (VocabularyChange change) => Locked(() => ChangeVocabulary(change)));
		app.MapGet("/api/photos", (HttpRequest request, int? offset) =>
			Locked(() => Photos(request.Query["source"].Where(s => s is not null).Select(s => s!).ToList(), offset ?? 0)));
		app.MapGet("/api/photo/{key}", (string key) => Locked(() => Photo(key)));

		await app.StartAsync(cancellationToken);
		string url = $"{app.Urls.First()}/?token={token}";
		output.WriteLine("The review page: " + url);
		output.WriteLine("Edit and approve there. Ctrl+C stops it; what you saved stays in the file.");
		if (openBrowser)
			OpenBrowser(url);

		bool done;
		try
		{
			done = await approved.Task.WaitAsync(cancellationToken);
			// Let the page receive the answer before the server goes away.
			await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
		}
		catch (OperationCanceledException)
		{
			done = false;
		}

		await app.StopAsync(CancellationToken.None);
		output.WriteLine(done ? "Approved. Run 'haru-data run' to continue." : "Stopped without an approval.");
		return done ? ExitCodes.Ok : ExitCodes.NeedsReview;
	}

	private T Locked<T>(Func<T> action)
	{
		// One SQLite connection for every request.
		lock (_lock)
			return action();
	}

	private object State()
	{
		var state = _review.Current(connection);
		if (state.Translations)
		{
			var file = TranslationsFile.Read(workspace.TranslationsPath);
			return new
			{
				stage = "translations",
				path = workspace.TranslationsPath,
				state.Approved,
				translations = file.Tags,
				// Every tag of the approved vocabulary: merge targets, and the photos of a translated tag.
				vocabulary = VocabDb.ReadApprovedTags(connection),
				check = Check(new Model { Translations = file.Tags })
			};
		}

		var vocabulary = VocabularyFile.Read(workspace.VocabularyPath);
		return new
		{
			stage = "vocabulary",
			path = workspace.VocabularyPath,
			state.Approved,
			tags = vocabulary.Tags,
			dropped = vocabulary.Dropped,
			check = Check(new Model { Tags = vocabulary.Tags, Dropped = vocabulary.Dropped })
		};
	}

	/// <summary>The problems of the edited model, as review --check would find them, and the photo counts.</summary>
	private CheckResult Check(Model model)
	{
		var (_, result) = Render(model);
		var lines = result.Tags.Select(t => t.Line).ToList();
		// The page shows no line numbers: "also used on line 12" becomes "also used by 'shop'".
		string Named(string message) => LineReference.Replace(message, match =>
			lines.IndexOf(int.Parse(match.Groups[2].Value)) is var index and >= 0
				? $"{(match.Groups[1].Value == "on" ? "by" : "in the tag")} '{result.Tags[index].Labels["en"]}'"
				: match.Value);
		int? Other(string message) => LineReference.Match(message) is { Success: true } match
			&& lines.IndexOf(int.Parse(match.Groups[2].Value)) is var index and >= 0 ? index : null;
		int? Index(int line) => lines.IndexOf(line) is var index and >= 0 ? index : null;
		var issues = result.Issues
			.Select(i => new PageIssue(Index(i.Line), i.Slug, Named(i.Message), i.IsError, i.Message.Contains("label") ? Other(i.Message) : null))
			.ToList();

		var keysBySource = KeysBySource();
		int Photos(IEnumerable<string> sources, IEnumerable<string>? excluded = null)
			=> sources.SelectMany(s => keysBySource.GetValueOrDefault(s) ?? []).Distinct().Except(excluded ?? []).Count();
		return new CheckResult(issues,
			model.Tags?.Select(t => Photos(t.Sources, t.Excluded)).ToList() ?? [],
			model.Dropped?.Select(d => Photos(d.Sources)).ToList() ?? []);
	}

	public sealed record CheckResult(List<PageIssue> Issues, List<int> Photos, List<int> DroppedPhotos);

	private CheckResult Save(Model model)
	{
		var (text, _) = Render(model, withProblems: true);
		string path = model.Translations is null ? workspace.VocabularyPath : workspace.TranslationsPath;
		File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
		return Check(model);
	}

	public sealed record ApproveResult(bool Ok, string Message, CheckResult Check);

	private ApproveResult Approve(Model model)
	{
		var check = Save(model);
		if (check.Issues.Any(i => i.IsError))
			return new ApproveResult(false, "Not approved: fix the errors first.", check);

		var report = new StepReport("review");
		string status = _review.Run(connection, ReviewStep.Mode.Approve, report);
		report.Finish(workspace, status);
		return new ApproveResult(status == "ok", report.Message, check);
	}

	/// <summary>
	/// Deletes a tag or merges it into another one while the translations are reviewed. The change
	/// goes into vocabulary.yaml, which is approved again at once: it was approved, and the person
	/// just made the change. The translations of the other tags stay as they are on the page.
	/// </summary>
	private object ChangeVocabulary(VocabularyChange change)
	{
		if (!_review.Current(connection).Translations)
			return new { ok = false, message = "The tag list is not approved yet; change it on its page." };

		var file = VocabularyFile.Read(workspace.VocabularyPath);
		var translations = change.Translations;
		var from = file.Tags.FirstOrDefault(t => t.Slug == change.Slug);
		if (from is null)
			return new { ok = false, message = $"There is no tag '{change.Slug}'." };
		var fromTranslation = translations.FirstOrDefault(t => t.Slug == from.Slug);

		string message;
		if (change.Action == "merge")
		{
			var into = file.Tags.FirstOrDefault(t => t.Slug == change.Into && t != from);
			if (into is null)
				return new { ok = false, message = $"There is no tag '{change.Into}' to merge into." };
			MergeTag(from, into);
			if (fromTranslation is not null && translations.FirstOrDefault(t => t.Slug == into.Slug) is { } intoTranslation)
				MergeTranslation(fromTranslation, intoTranslation);
			message = $"'{from.En}' is merged into '{into.En}'.";
		}
		else
		{
			file.Dropped.Insert(0, new VocabularyFile.DroppedEntry { Tag = from.En, Reason = "deleted in the review", Sources = from.Sources, Photos = from.Photos });
			message = $"'{from.En}' is deleted.";
		}
		file.Tags.Remove(from);
		if (fromTranslation is not null)
			translations.Remove(fromTranslation);

		// The vocabulary, approved again; a new approval starts new translations, so they are saved after it.
		var check = Save(new Model { Tags = file.Tags, Dropped = file.Dropped });
		if (check.Issues.Any(i => i.IsError))
			return new { ok = false, message = "The tag list has errors now; fix them on its page (haru-data review)." };
		var report = new StepReport("review");
		string status = _review.Run(connection, ReviewStep.Mode.Approve, report);
		report.Finish(workspace, status);
		if (status != "ok")
			return new { ok = false, message = report.Message };

		var (text, result) = Render(new Model { Translations = translations }, withProblems: true);
		File.WriteAllText(workspace.TranslationsPath, text, new System.Text.UTF8Encoding(false));
		VocabDb.SaveTranslations(connection, result.Tags, VocabDb.FileSha256(workspace.VocabularyPath), null);
		return new { ok = true, message };
	}

	/// <summary>
	/// The tag takes in the other one: its sources, so its photos get the tag, and its name and
	/// aliases as search words. Photos taken off the other tag get this one: check them in its photos.
	/// </summary>
	internal static void MergeTag(VocabularyFile.Entry from, VocabularyFile.Entry into)
	{
		into.Sources = into.Sources.Concat(from.Sources).Distinct(StringComparer.Ordinal).ToList();
		foreach (string word in from.Aliases.Prepend(from.En))
			if (!string.Equals(word, into.En, StringComparison.OrdinalIgnoreCase) && !into.Aliases.Contains(word, StringComparer.OrdinalIgnoreCase))
				into.Aliases.Add(word);
		into.Photos += from.Photos;
	}

	/// <summary>The other tag's names and search words in every language become search words of this one.</summary>
	internal static void MergeTranslation(TranslationsFile.Entry from, TranslationsFile.Entry into)
	{
		var labels = new[] { into.En, into.Ru, into.Ja };
		foreach (var (language, label) in new[] { ("en", from.En), ("ru", from.Ru), ("ja", from.Ja) })
		{
			if (!into.Aliases.TryGetValue(language, out var list))
				into.Aliases[language] = list = [];
			foreach (string word in (from.Aliases.GetValueOrDefault(language) ?? []).Prepend(label).Where(w => w.Length > 0))
				if (!labels.Contains(word, StringComparer.OrdinalIgnoreCase) && !list.Contains(word, StringComparer.OrdinalIgnoreCase))
					list.Add(word);
		}
	}

	/// <summary>
	/// The file text of the model and its check. With the problems written at the top, the text is
	/// checked once more so the line numbers there are those of the final file.
	/// </summary>
	private (string Text, VocabularyCheck.Result Result) Render(Model model, bool withProblems = false)
	{
		Func<IEnumerable<string>, string> render;
		Func<string, VocabularyCheck.Result> check;
		if (model.Translations is { } translations)
		{
			var newSlugs = VocabDb.ReadApproved(connection).Where(t => t.ExistingId is null).Select(t => t.Slug).ToList();
			render = problems => new TranslationsFile { Tags = translations }.Render(problems);
			check = text => VocabularyCheck.RunTranslations(text, newSlugs);
		}
		else
		{
			var known = KeysBySource().Keys.ToHashSet(StringComparer.Ordinal);
			var file = new VocabularyFile { Tags = model.Tags ?? [], Dropped = model.Dropped ?? [] };
			render = file.Render;
			check = text => VocabularyCheck.Run(text, known);
		}

		static List<string> Problems(VocabularyCheck.Result result)
			=> result.Issues.Select(i => $"{(i.IsError ? "error" : "warning")}: line {i.Line}{(i.Slug is null ? "" : $" ({i.Slug})")}: {i.Message}").ToList();

		string text = render([]);
		var result = check(text);
		if (!withProblems)
			return (text, result);

		result = check(render(Problems(result)));
		text = render(Problems(result));
		return (text, check(text));
	}

	private Dictionary<string, HashSet<string>> KeysBySource()
		=> AnalysisData.ReadRawTags(connection).GroupBy(r => r.Tag, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.Select(r => r.Key).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

	/// <summary>A page of 24 files with any of the sources, from <paramref name="offset"/>, and their total count.</summary>
	private object Photos(List<string> sources, int offset)
	{
		var keysBySource = KeysBySource();
		var keys = sources.SelectMany(s => keysBySource.GetValueOrDefault(s) ?? []).Distinct().Order(StringComparer.Ordinal).ToList();
		var media = AnalysisData.ReadMedia(connection).ToDictionary(m => m.Key, StringComparer.Ordinal);
		return new
		{
			total = keys.Count,
			files = keys.Skip(offset).Take(24).Where(media.ContainsKey).Select(k => new
			{
				key = k,
				name = Path.GetFileName(media[k].Path),
				shown = media[k].Type == "video" || BrowserImages.ContainsKey(Path.GetExtension(media[k].Path))
			})
		};
	}

	/// <summary>The photo itself, or the frame of a video.</summary>
	private IResult Photo(string key)
	{
		var item = AnalysisData.ReadMedia(connection).FirstOrDefault(m => m.Key == key);
		if (item is null)
			return Results.NotFound();

		string path = item.Type == "video" ? Path.Combine(workspace.Root, "frames", key + ".jpg") : item.Path;
		return File.Exists(path) && BrowserImages.TryGetValue(Path.GetExtension(path), out string? type)
			? Results.File(path, type)
			: Results.NotFound();
	}

	private static string Page()
	{
		using var stream = typeof(ReviewServer).Assembly.GetManifestResourceStream("review.html")
			?? throw new InvalidOperationException("review.html is not embedded");
		using var reader = new StreamReader(stream);
		return reader.ReadToEnd();
	}

	private static void OpenBrowser(string url)
	{
		try
		{
			Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			// No browser to start: the address is printed, it can be opened by hand.
		}
	}
}
