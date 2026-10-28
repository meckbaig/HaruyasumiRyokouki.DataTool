using HaruyasumiRyokouki.DataTool.Common;
using HaruyasumiRyokouki.DataTool.Tags.Vocabulary;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Tags.Site;

/// <summary>
/// Step assign: adds every approved tag to the site files that have one of its sources. It uses
/// POST /v1/tags/{id}/media, which only adds, so tags a person put on a file stay and a second
/// run changes nothing.
/// </summary>
internal sealed class AssignStep(BackendApi api, string vocabularyPath, Confirm confirm, TextWriter output)
{
	/// <summary>Files per request.</summary>
	public int BatchSize { get; init; } = 500;

	public async Task<string?> RunAsync(SqliteConnection connection, StepReport report, CancellationToken cancellationToken)
	{
		VocabDb.RequireApproved(connection, vocabularyPath);
		SiteDb.Ensure(connection);
		var tags = VocabDb.ReadApproved(connection);
		var tagIds = SiteDb.ReadTags(connection);
		var mediaIds = SiteDb.ReadMedia(connection);

		var noId = tags.Where(t => !tagIds.ContainsKey(t.Slug)).Select(t => t.Slug).ToList();
		if (noId.Count > 0)
			throw new WorkspaceException($"{noId.Count} tags are not on the site yet ({string.Join(", ", noId.Take(5))}…). Run haru-tags push-tags.");
		if (mediaIds.Count == 0)
			throw new WorkspaceException("No file has a site id yet. Run haru-tags upload and haru-tags sync.");

		var work = tags
			.Select(t => (t.Slug, Id: tagIds[t.Slug], Files: t.Keys.Where(mediaIds.ContainsKey).Select(k => mediaIds[k]).Order().ToList()))
			.Where(w => w.Files.Count > 0)
			.ToList();
		report.Count("tags without files on the site", tags.Count - work.Count);
		int links = work.Sum(w => w.Files.Count);

		await api.EnsureReachableAsync(cancellationToken);
		if (!confirm($"Add {work.Count} tags to the site files ({links} links; tags already on a file stay as they are):",
			["API: " + Confirmation.Host(api.BaseAddress.ToString())]))
		{
			report.Message = "not confirmed, nothing added";
			return "needs_review";
		}

		report.Count("links added", 0);
		report.Count("links already there", 0);
		foreach (var (slug, id, files) in work)
		{
			try
			{
				foreach (var batch in files.Chunk(BatchSize))
				{
					int affected = await api.AddTagToMediaAsync(id, batch, cancellationToken);
					report.Count("links added", affected);
					report.Count("links already there", batch.Length - affected);
				}
			}
			catch (InvalidOperationException ex)
			{
				report.Error(ex.Message, slug);
				output.WriteLine("error: " + ex.Message);
			}
		}

		report.Message = $"added {report.Counts["links added"]} links, {report.Counts["links already there"]} were already there";
		return null;
	}
}
