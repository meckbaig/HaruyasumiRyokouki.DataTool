using HaruyasumiRyokouki.DataTool.Common;
using HaruyasumiRyokouki.DataTool.Tags.Vocabulary;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Tags.Site;

/// <summary>Asks before writing outside the workspace: what will happen, where; true to go on.</summary>
internal delegate bool Confirm(string action, IReadOnlyList<string> targets);

/// <summary>
/// Step push-tags: creates the tags of the approved vocabulary that the site does not have and
/// stores the id of every tag. A tag the site already has, by the id the vocab step matched or
/// by slug, is not created again, so a second run creates nothing. A tag the site refuses is an
/// error of the step, but the other tags are still created.
/// </summary>
internal sealed class PushTagsStep(BackendApi api, string vocabularyPath, Confirm confirm, TextWriter output)
{
	/// <summary>Returns the report status, or null to let the errors decide.</summary>
	public async Task<string?> RunAsync(SqliteConnection connection, StepReport report, CancellationToken cancellationToken)
	{
		VocabDb.RequireApproved(connection, vocabularyPath);
		SiteDb.Ensure(connection);
		var tags = VocabDb.ReadApproved(connection);

		await api.EnsureReachableAsync(cancellationToken);
		var siteTags = await api.GetTagsAsync(cancellationToken);
		var siteIds = siteTags.Select(t => t.Id).ToHashSet();
		var siteBySlug = siteTags.ToDictionary(t => t.Slug, t => t.Id, StringComparer.Ordinal);

		var toCreate = new List<ApprovedTag>();
		foreach (var tag in tags)
		{
			if (tag.ExistingId is int existing)
			{
				if (siteIds.Contains(existing))
				{
					SiteDb.SaveTag(connection, tag.Slug, existing, created: false);
					report.Count("already on the site");
				}
				else
					report.Error($"the vocabulary links it to site tag {existing}, which the site no longer has; run vocab again", tag.Slug);
			}
			else if (siteBySlug.TryGetValue(tag.Slug, out int id))
			{
				SiteDb.SaveTag(connection, tag.Slug, id, created: false);
				report.Count("already on the site");
			}
			else
				toCreate.Add(tag);
		}

		report.Count("created", 0);
		if (toCreate.Count == 0)
		{
			report.Message = "the site has every tag";
			return null;
		}

		if (!confirm($"Create {toCreate.Count} tags on the site:", ["API: " + Confirmation.Host(api.BaseAddress.ToString())]))
		{
			report.Message = "not confirmed, nothing created";
			return "needs_review";
		}

		foreach (var tag in toCreate)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				var translations = Translator.Languages.Select(l => new TagText(l, tag.Labels[l]));
				int id = await api.AddTagAsync(tag.Slug, translations, tag.Aliases, cancellationToken);
				SiteDb.SaveTag(connection, tag.Slug, id, created: true);
				report.Count("created");
			}
			catch (InvalidOperationException ex)
			{
				// The site's validation refused the tag; the others may still be fine.
				report.Error(ex.Message, tag.Slug);
				output.WriteLine("error: " + ex.Message);
			}
		}

		report.Message = report.Errors.Count == 0
			? $"created {report.Counts["created"]} tags"
			: "some tags were not created: fix them in vocabulary.yaml, approve it and run push-tags again";
		return null;
	}
}
