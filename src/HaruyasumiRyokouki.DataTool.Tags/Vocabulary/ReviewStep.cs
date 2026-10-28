using System.Security.Cryptography;
using HaruyasumiRyokouki.DataTool.Common;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

/// <summary>
/// Step review: the person's part of the vocabulary. Without options it shows a summary and whether
/// the current vocabulary.yaml is approved; --check checks the edited file with line numbers;
/// --approve checks it and makes it the vocabulary the next steps write to the site. Editing the
/// file after the approval takes the approval back: the hash of the approved file is stored.
/// </summary>
internal sealed class ReviewStep(string vocabularyPath, TextWriter output)
{
	public enum Mode { Summary, Check, Approve }

	/// <summary>Returns the report status: ok, error or needs_review.</summary>
	public string Run(SqliteConnection connection, Mode mode, StepReport report)
	{
		AnalysisData.Require(connection);
		VocabDb.Ensure(connection);
		if (!File.Exists(vocabularyPath))
			throw new WorkspaceException($"{vocabularyPath} not found. Run haru-tags vocab first.");

		var rawTags = AnalysisData.ReadRawTags(connection);
		var keysBySource = rawTags.GroupBy(r => r.Tag, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.Select(r => r.Key).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

		byte[] bytes = File.ReadAllBytes(vocabularyPath);
		string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
		var result = VocabularyCheck.Run(new System.Text.UTF8Encoding(false).GetString(bytes), keysBySource.Keys.ToHashSet(StringComparer.Ordinal));

		PrintSummary(result, rawTags, report);
		foreach (var issue in result.Issues)
			output.WriteLine($"{(issue.IsError ? "error" : "warning")}: line {issue.Line}{(issue.Slug is null ? "" : $" ({issue.Slug})")}: {issue.Message}");
		report.Count("errors in file", result.Issues.Count(i => i.IsError));
		report.Count("warnings in file", result.Issues.Count(i => !i.IsError));

		if (mode == Mode.Approve)
		{
			if (result.HasErrors)
			{
				report.Message = "not approved: fix the errors in vocabulary.yaml";
				return "error";
			}
			VocabDb.Approve(connection, result.Tags, keysBySource, sha256);
			report.Message = $"approved {result.Tags.Count} tags";
			output.WriteLine(report.Message);
			return "ok";
		}

		if (mode == Mode.Check)
		{
			report.Message = result.HasErrors ? "vocabulary.yaml has errors" : "vocabulary.yaml is valid";
			return result.HasErrors ? "error" : "ok";
		}

		bool approved = VocabDb.ApprovedSha256(connection) == sha256;
		report.Message = approved
			? "approved; the next steps can write to the site"
			: "waiting for review: edit vocabulary.yaml if needed, then run haru-tags review --approve";
		output.WriteLine(report.Message);
		return approved ? "ok" : "needs_review";
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
