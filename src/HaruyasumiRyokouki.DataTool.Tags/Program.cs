using System.ClientModel;
using System.CommandLine;
using HaruyasumiRyokouki.DataTool.Common;
using HaruyasumiRyokouki.DataTool.Tags;
using HaruyasumiRyokouki.DataTool.Tags.Llm;
using HaruyasumiRyokouki.DataTool.Tags.Site;
using HaruyasumiRyokouki.DataTool.Tags.Vocabulary;
using Microsoft.Data.Sqlite;
using Npgsql;

// Labels in Russian and Japanese must not turn into question marks in a Windows console.
Console.OutputEncoding = System.Text.Encoding.UTF8;
return Cli.Build().Parse(args).Invoke();

namespace HaruyasumiRyokouki.DataTool.Tags
{
	/// <summary>
	/// haru-tags &lt;step&gt; --workspace &lt;folder&gt;. Every step reads the workspace, writes
	/// reports/&lt;step&gt;.json and exits with 0, 1 or 2 (docs/formats.md).
	/// </summary>
	internal static class Cli
	{
		public static readonly Option<string> WorkspaceOption = new("--workspace")
		{
			Description = "workspace folder with haru-data.toml",
			Required = true,
			Recursive = true
		};

		public static RootCommand Build()
		{
			var root = new RootCommand("Tag vocabulary and import into the HaruyasumiRyokouki site.");
			root.Options.Add(WorkspaceOption);

			var status = new Command("status", "show what haru-analysis found and what this app has done");
			status.SetAction(parse => Run(parse, Status));
			root.Subcommands.Add(status);

			var vocab = new Command("vocab", "build the tag vocabulary from the machine tags with the model in [usage] vocabulary");
			vocab.SetAction((parse, cancellationToken) => RunStepAsync(parse, "vocab", async (workspace, db, report) =>
			{
				var chat = new ChatService(LlmProvider.FromWorkspace(workspace, "vocabulary"), workspace.LlmCacheDir);
				using var api = BackendApi.FromWorkspace(workspace);
				// The backend's AI may be a free tier with a low request rate; raise it for a paid one.
				var translator = new Translator(api, workspace.LlmCacheDir, workspace.GetInt("vocabulary.translate_concurrency") ?? 4, Console.Out);
				await new VocabStep(chat, api, translator, workspace.VocabularyPath, Console.Out).RunAsync(db, report, cancellationToken);
				return null;
			}, cancellationToken));
			root.Subcommands.Add(vocab);

			var check = new Option<bool>("--check") { Description = "check the edited vocabulary.yaml, with line numbers" };
			var approve = new Option<bool>("--approve") { Description = "check vocabulary.yaml and make it the vocabulary written to the site" };
			var review = new Command("review", "summary of the vocabulary and whether it is approved; exits with 2 while it waits for review");
			review.Options.Add(check);
			review.Options.Add(approve);
			review.Validators.Add(result =>
			{
				if (result.GetValue(check) && result.GetValue(approve))
					result.AddError("use either --check or --approve");
			});
			review.SetAction((parse, cancellationToken) => RunStepAsync(parse, "review", (workspace, db, report) =>
			{
				var mode = parse.GetValue(approve) ? ReviewStep.Mode.Approve : parse.GetValue(check) ? ReviewStep.Mode.Check : ReviewStep.Mode.Summary;
				return Task.FromResult<string?>(new ReviewStep(workspace.VocabularyPath, Console.Out).Run(db, mode, report));
			}, cancellationToken));
			root.Subcommands.Add(review);

			var pushTags = new Command("push-tags", "create the approved tags the site does not have");
			pushTags.Options.Add(YesOption);
			pushTags.SetAction((parse, cancellationToken) => RunStepAsync(parse, "push-tags", async (workspace, db, report) =>
			{
				using var api = BackendApi.FromWorkspace(workspace);
				return await new PushTagsStep(api, workspace.VocabularyPath, Confirmer(parse), Console.Out).RunAsync(db, report, cancellationToken);
			}, cancellationToken));
			root.Subcommands.Add(pushTags);

			var upload = new Command("upload", "copy the source files the storage does not have into the storage of the site");
			upload.Options.Add(YesOption);
			upload.SetAction((parse, cancellationToken) => RunStepAsync(parse, "upload", async (workspace, db, report) =>
			{
				using var storage = MediaStorage.FromWorkspace(workspace);
				var step = new UploadStep(storage, workspace.GetInt("storage.concurrency") ?? 4, Confirmer(parse), Console.Out);
				return await step.RunAsync(db, report, cancellationToken);
			}, cancellationToken));
			root.Subcommands.Add(upload);

			var sync = new Command("sync", "let the site import the new files of its storage and read their ids");
			sync.Options.Add(YesOption);
			sync.SetAction((parse, cancellationToken) => RunStepAsync(parse, "sync", async (workspace, db, report) =>
			{
				using var api = BackendApi.FromWorkspace(workspace);
				// Only to tell "not uploaded" from "skipped by the site" in the report.
				using var storage = workspace.GetString("storage.type") is null ? null : MediaStorage.FromWorkspace(workspace);
				var step = new SyncStep(api, storage, SiteDatabase.ConnectionString(), Confirmer(parse), Console.Out);
				return await step.RunAsync(db, report, cancellationToken);
			}, cancellationToken));
			root.Subcommands.Add(sync);

			var assign = new Command("assign", "add the approved tags to the site files; tags already on a file stay");
			assign.Options.Add(YesOption);
			assign.SetAction((parse, cancellationToken) => RunStepAsync(parse, "assign", async (workspace, db, report) =>
			{
				using var api = BackendApi.FromWorkspace(workspace);
				return await new AssignStep(api, workspace.VocabularyPath, Confirmer(parse), Console.Out).RunAsync(db, report, cancellationToken);
			}, cancellationToken));
			root.Subcommands.Add(assign);

			var dryRun = new Option<bool>("--dry-run") { Description = "match the vectors with the site files and report, write nothing" };
			var pushEmbeddings = new Command("push-embeddings", "write the image vectors into media_embeddings of the site's database");
			pushEmbeddings.Options.Add(YesOption);
			pushEmbeddings.Options.Add(dryRun);
			pushEmbeddings.SetAction((parse, cancellationToken) => RunStepAsync(parse, "push-embeddings", (workspace, db, report) =>
				new PushEmbeddingsStep(SiteDatabase.ConnectionString(), parse.GetValue(dryRun), Confirmer(parse), Console.Out)
					.RunAsync(db, report, cancellationToken), cancellationToken));
			root.Subcommands.Add(pushEmbeddings);

			return root;
		}

		public static readonly Option<bool> YesOption = new("--yes")
		{
			Description = "do not ask before writing to the site; the target is still printed"
		};

		private static Confirm Confirmer(ParseResult parse)
			=> (action, targets) => Confirmation.Ask(action, targets, parse.GetValue(YesOption));

		/// <summary>
		/// Runs a step with its report; the step may return its own status (needs_review), otherwise
		/// the report decides by its errors. A problem the user has to fix, a failed model or API call and
		/// Ctrl+C all end in a report with status error and exit code 1; work saved so far stays saved.
		/// </summary>
		private static async Task<int> RunStepAsync(ParseResult parse, string step, Func<Workspace, SqliteConnection, StepReport, Task<string?>> run,
			CancellationToken cancellationToken = default)
		{
			Workspace workspace;
			try
			{
				workspace = Workspace.Open(parse.GetValue(WorkspaceOption)!);
			}
			catch (WorkspaceException ex)
			{
				Console.Error.WriteLine("error: " + ex.Message);
				return ExitCodes.Error;
			}

			var report = new StepReport(step);
			string? status;
			try
			{
				using var db = WorkDb.Open(workspace);
				status = await run(workspace, db, report);
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				// Not Ctrl+C: a request ran out of its time limit.
				Console.Error.WriteLine("error: a request to the server timed out");
				report.Error("a request to the server timed out");
				return report.Finish(workspace);
			}
			catch (OperationCanceledException)
			{
				Console.Error.WriteLine("interrupted, run the step again to continue");
				report.Message = "interrupted";
				return report.Finish(workspace, "error");
			}
			catch (Exception ex) when (ex is WorkspaceException or InvalidOperationException or ClientResultException or HttpRequestException
				or NpgsqlException or IOException)
			{
				Console.Error.WriteLine("error: " + ex.Message);
				report.Error(ex.Message);
				return report.Finish(workspace);
			}

			Console.WriteLine($"{step}: " + string.Join(", ", report.Counts.Select(c => $"{c.Key} {c.Value}")));
			return report.Finish(workspace, status);
		}

		/// <summary>Opens the workspace and turns problems the user has to fix into one line and exit code 1.</summary>
		private static int Run(ParseResult parse, Func<Workspace, int> step)
		{
			try
			{
				return step(Workspace.Open(parse.GetValue(WorkspaceOption)!));
			}
			catch (WorkspaceException ex)
			{
				Console.Error.WriteLine("error: " + ex.Message);
				return ExitCodes.Error;
			}
		}

		private static int Status(Workspace workspace)
		{
			using var db = WorkDb.Open(workspace);
			AnalysisData.Require(db);

			foreach (var (name, count) in AnalysisData.Summary(db))
				Console.WriteLine($"{name}: {count}");

			return ExitCodes.Ok;
		}
	}
}
