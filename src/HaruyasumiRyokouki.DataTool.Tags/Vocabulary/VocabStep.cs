using HaruyasumiRyokouki.DataTool.Common;
using HaruyasumiRyokouki.DataTool.Tags.Llm;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

/// <summary>
/// Step vocab: from the raw tags and names of all photos to a vocabulary. Mechanical normalization
/// first, then one request to find duplicates in the whole list, then keep-or-drop in batches.
/// </summary>
internal sealed class VocabStep(ChatService chat, TextWriter output)
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

		var warnings = new List<string>();
		var tags = await MergeDuplicatesAsync(connection, entries, warnings, cancellationToken);
		report.Count("tags", tags.Count);
		await DecideKeepOrDropAsync(tags, warnings, cancellationToken);

		var problems = warnings.Select(w => ("model", (string?)null, w)).ToList();
		VocabDb.Save(connection, tags, problems);

		report.Count("kept", tags.Count(t => t.Keep));
		report.Count("dropped", tags.Count(t => !t.Keep));
		report.Count("warnings", warnings.Count);
		report.Count("llm requests", chat.Usage.Requests);
		report.Count("llm cached answers", chat.Usage.CachedAnswers);
		report.Count("input tokens", chat.Usage.InputTokens);
		report.Count("output tokens", chat.Usage.OutputTokens);
		report.Count("reasoning tokens", chat.Usage.ReasoningTokens);
		report.Message = chat.Describe();
		foreach (string warning in warnings)
			output.WriteLine("warning: " + warning);
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
