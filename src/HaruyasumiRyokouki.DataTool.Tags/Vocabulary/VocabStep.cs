using HaruyasumiRyokouki.DataTool.Common;
using HaruyasumiRyokouki.DataTool.Tags.Llm;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

/// <summary>
/// Step vocab: from the raw tags and names of all photos to an English vocabulary. Mechanical
/// normalization first, then one request to find duplicates in the whole list, keep-or-drop in
/// batches, matching with the site's tags, checks, and vocabulary.yaml for the review. Nothing is
/// translated here: the translate step does it after the review, for the tags that are left.
/// </summary>
internal sealed class VocabStep(ChatService chat, BackendApi api, string vocabularyPath, TextWriter output)
{
	public async Task RunAsync(SqliteConnection connection, StepReport report, CancellationToken cancellationToken = default)
	{
		AnalysisData.Require(connection);
		VocabDb.Ensure(connection);

		var rawTags = AnalysisData.ReadRawTags(connection);
		var entries = VocabEntry.Build(rawTags);
		report.Count("raw tags", rawTags.Select(r => r.Tag).Distinct().Count());
		report.Count("entries", entries.Count);
		if (entries.Count == 0)
		{
			report.Message = "no tags yet: run haru-analysis tag first";
			return;
		}

		// Before the model is paid: the site's tags are matched, so the backend must answer.
		await api.EnsureReachableAsync(cancellationToken);
		var existing = await api.GetTagsAsync(cancellationToken);
		report.Count("site tags", existing.Count);

		var warnings = new List<string>();
		var tags = await MergeDuplicatesAsync(connection, entries, warnings, cancellationToken);
		report.Count("tags", tags.Count);
		await DecideKeepOrDropAsync(tags, warnings, cancellationToken);

		Translator.MatchExisting(tags, existing);
		foreach (var tag in tags.Where(t => t.Keep))
			Translator.SetEnglish(tag);

		var problems = warnings.Select(w => new Checks.Problem("model", null, w))
			.Concat(Checks.Run(tags))
			.ToList();
		VocabDb.Save(connection, tags, problems);
		VocabularyFile.From(tags).Write(vocabularyPath, problems.Select(p => (p.Tag is null ? "" : p.Tag + ": ") + p.Message));

		report.Count("kept", tags.Count(t => t.Keep));
		report.Count("dropped", tags.Count(t => !t.Keep));
		report.Count("already on the site", tags.Count(t => t.Keep && t.ExistingId is not null));
		report.Count("problems", problems.Count);
		report.Count("warnings", warnings.Count);
		report.Count("llm requests", chat.Usage.Requests);
		report.Count("llm cached answers", chat.Usage.CachedAnswers);
		report.Count("input tokens", chat.Usage.InputTokens);
		report.Count("output tokens", chat.Usage.OutputTokens);
		report.Count("reasoning tokens", chat.Usage.ReasoningTokens);
		report.Message = $"{chat.Describe()}; review {vocabularyPath}";
		foreach (var problem in problems)
			output.WriteLine($"problem: {(problem.Tag is null ? "" : problem.Tag + ": ")}{problem.Message}");
	}

	/// <summary>
	/// What a run would send, without sending anything: requests and rough input tokens to the
	/// vocabulary model, and translation requests to the backend's AI. Answers already in the
	/// cache are not paid again, so a rerun costs less than this.
	/// </summary>
	public static void Estimate(SqliteConnection connection, LlmProvider provider, TextWriter output)
	{
		AnalysisData.Require(connection);
		var entries = VocabEntry.Build(AnalysisData.ReadRawTags(connection));
		if (entries.Count == 0)
		{
			output.WriteLine("vocab estimate: no tags yet, nothing to send");
			return;
		}

		var groups = entries.Select(e =>
		{
			var group = new TagGroup(e.Text);
			group.Keys.UnionWith(e.Keys);
			return group;
		}).ToList();
		string dedupe = Dedupe.Prompt + Dedupe.BuildMessage(entries, DuplicateHints.Build(groups, AnalysisData.ReadVectors(connection)));
		// The filter asks about the merged tags; the entries are an upper bound.
		var batches = entries.Chunk(Filter.BatchSize).ToList();
		long filterChars = batches.Sum(b => Filter.Prompt.Length + Filter.BuildMessage(b.Select(e => new Filter.Item(e.Text, e.Keys.Count, null))).Length);

		// About 4 characters per token for English text in JSON.
		long inputTokens = (dedupe.Length + filterChars) / 4;
		output.WriteLine($"vocab estimate for {provider.Name} ({provider.Model}):");
		output.WriteLine($"  {1 + batches.Count} requests, about {inputTokens:N0} input tokens; the answers are shorter, thinking models add their reasoning");
		output.WriteLine("  answers in cache/llm from an earlier run are not sent again");
	}

	private async Task<List<VocabTag>> MergeDuplicatesAsync(
		SqliteConnection connection, List<VocabEntry> entries, List<string> warnings, CancellationToken cancellationToken)
	{
		var groups = entries.Select(e =>
		{
			var group = new TagGroup(e.Text);
			group.Keys.UnionWith(e.Keys);
			return group;
		}).ToList();
		var hints = DuplicateHints.Build(groups, AnalysisData.ReadVectors(connection));

		output.WriteLine($"vocab: finding duplicates among {entries.Count} entries, one request");
		var answer = await chat.GetJsonResponseAsync<Dedupe.Answer>(
			Dedupe.BuildMessage(entries, hints), Dedupe.Prompt,
			a => a.Merge is null || a.Fix is null ? "both \"merge\" and \"fix\" must be present, even if empty" : null,
			cancellationToken);
		var nameOf = Dedupe.Apply(entries, answer, warnings);

		var tags = new Dictionary<string, VocabTag>(StringComparer.Ordinal);
		foreach (var entry in entries)
		{
			string name = nameOf[entry];
			if (!tags.TryGetValue(name, out var tag))
				tags[name] = tag = new VocabTag(name);

			tag.Entries.Add(entry);
			tag.Keys.UnionWith(entry.Keys);
			tag.RawTags.UnionWith(entry.RawTags);
			// A merge of a general tag and a name is a name: "tokyo" the tag and "tokyo" the place.
			if (tag.Kind == "tag" && entry.Kind != "tag")
				tag.Kind = entry.Kind;
		}

		return tags.Values.OrderByDescending(t => t.Keys.Count).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
	}

	private async Task DecideKeepOrDropAsync(List<VocabTag> tags, List<string> warnings, CancellationToken cancellationToken)
	{
		var byName = tags.ToDictionary(t => t.Name, StringComparer.Ordinal);
		var batches = tags.Chunk(Filter.BatchSize).ToList();

		for (int i = 0; i < batches.Count; i++)
		{
			output.WriteLine($"vocab: keep or drop, batch {i + 1}/{batches.Count}");
			var items = batches[i].Select(t => new Filter.Item(t.Name, t.Keys.Count, Names(t))).ToList();
			int expected = items.Count;
			var answer = await chat.GetJsonResponseAsync<Filter.Answer>(
				Filter.BuildMessage(items), Filter.Prompt,
				a => a.Decisions is null || a.Decisions.Count < expected / 2 || a.Decisions.Any(d => d.Tag is null || d.Action is null)
					? $"expected a decision with \"tag\", \"action\" and \"reason\" for each of the {expected} items"
					: null,
				cancellationToken);

			var decided = new HashSet<string>(StringComparer.Ordinal);
			foreach (var decision in answer.Decisions ?? [])
			{
				if (!byName.TryGetValue(decision.Tag?.Trim() ?? "", out var tag))
				{
					warnings.Add($"Keep or drop: the model answered for a tag that was not asked: '{decision.Tag}'.");
					continue;
				}
				decided.Add(tag.Name);
				tag.Keep = !string.Equals(decision.Action, "drop", StringComparison.OrdinalIgnoreCase);
				tag.Reason = tag.Keep ? null : decision.Reason;
			}

			// No decision means keep: losing a tag silently is worse than reviewing one more.
			foreach (var tag in batches[i].Where(t => !decided.Contains(t.Name)))
				warnings.Add($"Keep or drop: no decision for '{tag.Name}'; kept.");
		}
	}

	private static List<Filter.NameInfo>? Names(VocabTag tag)
	{
		var names = tag.Entries.Where(e => e.Kind != "tag").Select(e => new Filter.NameInfo(e.Text, e.Kind, e.Evidence)).ToList();
		return names.Count > 0 ? names : null;
	}
}
