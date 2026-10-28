using HaruyasumiRyokouki.DataTool.Common;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

/// <summary>
/// Step review: the person's part. It works on whatever waits for a person: vocabulary.yaml until
/// it is approved, then translations.yaml when the translate step found problems in it. Without
/// options it shows a summary and whether the file is approved; --check checks the edited file with
/// line numbers; --approve checks it and makes it what the next steps use. Editing an approved
/// file takes the approval back: the hash of the approved file is stored.
/// </summary>
internal sealed class ReviewStep(string vocabularyPath, string translationsPath, TextWriter output)
{
	public enum Mode { Summary, Check, Approve }

	/// <summary>Returns the report status: ok, error or needs_review.</summary>
	public string Run(SqliteConnection connection, Mode mode, StepReport report)
	{
		AnalysisData.Require(connection);
		VocabDb.Ensure(connection);
		if (!File.Exists(vocabularyPath))
			throw new WorkspaceException($"{vocabularyPath} not found. Run haru-data run (the vocab step) first.");

		string vocabulary = VocabDb.FileSha256(vocabularyPath);
		bool vocabularyApproved = VocabDb.ApprovedSha256(connection) == vocabulary;
		var translations = VocabDb.ReadTranslationState(connection);
		bool translated = vocabularyApproved && translations?.VocabularySha256 == vocabulary && File.Exists(translationsPath);

		// Checking or approving the vocabulary again is always possible until its translations exist.
		if (!vocabularyApproved || (!translated && mode != Mode.Summary))
			return RunVocabulary(connection, mode, report);

		if (!translated)
		{
			report.Message = "vocabulary.yaml is approved; the translate step translates the new tags next (haru-data run)";
			output.WriteLine(report.Message);
			return "ok";
		}

		return RunTranslations(connection, mode, report, vocabulary, translations!);
	}

	private string RunVocabulary(SqliteConnection connection, Mode mode, StepReport report)
	{
		var rawTags = AnalysisData.ReadRawTags(connection);
		var keysBySource = rawTags.GroupBy(r => r.Tag, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.Select(r => r.Key).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

		byte[] bytes = File.ReadAllBytes(vocabularyPath);
		var result = VocabularyCheck.Run(new System.Text.UTF8Encoding(false).GetString(bytes), keysBySource.Keys.ToHashSet(StringComparer.Ordinal));

		output.WriteLine($"== {vocabularyPath}");
		PrintSummary(result, rawTags, report);
		PrintIssues(result, report);

		if (mode == Mode.Approve)
		{
			if (result.HasErrors)
			{
				report.Message = "not approved: fix the errors in vocabulary.yaml";
				return "error";
			}
			VocabDb.Approve(connection, result.Tags, keysBySource, VocabDb.FileSha256(vocabularyPath));
			report.Message = $"approved {result.Tags.Count} tags; the new ones are translated next (haru-data run)";
			output.WriteLine(report.Message);
			return "ok";
		}

		if (mode == Mode.Check)
		{
			report.Message = result.HasErrors ? "vocabulary.yaml has errors" : "vocabulary.yaml is valid";
			return result.HasErrors ? "error" : "ok";
		}

		report.Message = "vocabulary.yaml waits for review: edit it if needed, then 'haru-data review --approve'";
		output.WriteLine(report.Message);
		return "needs_review";
	}

	private string RunTranslations(SqliteConnection connection, Mode mode, StepReport report, string vocabulary, VocabDb.TranslationState state)
	{
		var newSlugs = VocabDb.ReadApproved(connection).Where(t => t.ExistingId is null).Select(t => t.Slug).ToList();
		var result = VocabularyCheck.RunTranslations(File.ReadAllText(translationsPath), newSlugs);

		output.WriteLine($"== {translationsPath}");
		report.Count("new tags", newSlugs.Count);
		output.WriteLine($"{newSlugs.Count} new tags with translations");
		PrintIssues(result, report);

		if (mode == Mode.Approve)
		{
			if (result.HasErrors)
			{
				report.Message = "not approved: fix the errors in translations.yaml";
				return "error";
			}
			VocabDb.SaveTranslations(connection, result.Tags, vocabulary, VocabDb.FileSha256(translationsPath));
			report.Message = "approved the translations; the next steps write to the site (haru-data run)";
			output.WriteLine(report.Message);
			return "ok";
		}

		if (mode == Mode.Check)
		{
			report.Message = result.HasErrors ? "translations.yaml has errors" : "translations.yaml is valid";
			return result.HasErrors ? "error" : "ok";
		}

		bool approved = state.FileSha256 == VocabDb.FileSha256(translationsPath);
		report.Message = approved
			? "vocabulary.yaml and translations.yaml are approved; the next steps can write to the site"
			: "translations.yaml waits for review: fix it, then 'haru-data review --approve'";
		output.WriteLine(report.Message);
		return approved ? "ok" : "needs_review";
	}

	private void PrintIssues(VocabularyCheck.Result result, StepReport report)
	{
		foreach (var issue in result.Issues)
			output.WriteLine($"{(issue.IsError ? "error" : "warning")}: line {issue.Line}{(issue.Slug is null ? "" : $" ({issue.Slug})")}: {issue.Message}");
		report.Count("errors in file", result.Issues.Count(i => i.IsError));
		report.Count("warnings in file", result.Issues.Count(i => !i.IsError));
	}

	private void PrintSummary(VocabularyCheck.Result result, IReadOnlyList<RawTag> rawTags, StepReport report)
	{
		var tags = result.Tags;
		var usedSources = tags.SelectMany(t => t.Sources).ToHashSet(StringComparer.Ordinal);
		var allSources = rawTags.Select(r => r.Tag).ToHashSet(StringComparer.Ordinal);
		var files = rawTags.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
		var tagged = rawTags.Where(r => usedSources.Contains(r.Tag)).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);

		report.Count("tags", tags.Count);
		report.Count("new tags", tags.Count(t => !t.Existing));
		report.Count("tags already on the site", tags.Count(t => t.Existing));
		report.Count("machine tags not used", allSources.Count(s => !usedSources.Contains(s)));
		report.Count("files without any tag", files.Count - tagged.Count);

		output.WriteLine($"{tags.Count} tags: {tags.Count(t => !t.Existing)} new, {tags.Count(t => t.Existing)} already on the site");
		output.WriteLine($"{allSources.Count(s => !usedSources.Contains(s))} machine tags are not in any tag (dropped or removed)");
		output.WriteLine($"{files.Count - tagged.Count} of {files.Count} tagged files would get no tag");

		var photos = rawTags.GroupBy(r => r.Tag, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(r => r.Key).ToHashSet());
		var top = tags
			.Select(t => (t.Labels["en"], Count: t.Sources.SelectMany(s => photos.GetValueOrDefault(s) ?? []).Distinct().Count()))
			.OrderByDescending(t => t.Count).Take(15);
		output.WriteLine("most used: " + string.Join(", ", top.Select(t => $"{t.Item1} {t.Count}")));
	}
}
