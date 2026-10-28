using HaruyasumiRyokouki.DataTool.Common;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Tags.Site;

/// <summary>
/// Step sync: asks the site to import the new files of its storage, then reads media_files and
/// stores the site id of every file of the source folder by key. A file the site did not import
/// is a warning with the likely reason; two site files with one key are an error, since a tag
/// or a vector could go to the wrong one.
/// </summary>
internal sealed class SyncStep(BackendApi api, IMediaStorage? storage, string connectionString, Confirm confirm, TextWriter output)
{
	public async Task<string?> RunAsync(SqliteConnection connection, StepReport report, CancellationToken cancellationToken)
	{
		AnalysisData.Require(connection);
		SiteDb.Ensure(connection);
		var media = AnalysisData.ReadMedia(connection);

		await api.EnsureReachableAsync(cancellationToken);
		if (!confirm("Import the new files of the storage into the site, then read their ids:",
			["API: " + Confirmation.Host(api.BaseAddress.ToString()), SiteDatabase.Describe(connectionString)]))
		{
			report.Message = "not confirmed, nothing imported";
			return "needs_review";
		}

		output.WriteLine("syncing; the site converts photos and makes video previews, this can take a while");
		var started = DateTime.UtcNow;
		await api.SyncMediaAsync(output, cancellationToken);
		output.WriteLine($"sync finished in {(DateTime.UtcNow - started).TotalMinutes:F1} min");

		await using var db = await SiteDatabase.OpenAsync(connectionString, cancellationToken);
		var siteFiles = await SiteDatabase.ReadMediaFilesAsync(db, cancellationToken);
		var (byKey, collisions) = SiteDatabase.MatchKeys(siteFiles);

		var collided = collisions.ToDictionary(g => SiteDatabase.MediaKey(g[0].FileName), StringComparer.Ordinal);
		foreach (var item in media.Where(m => collided.ContainsKey(m.Key)))
			report.Error("the site has several files with this key: " + string.Join(", ", collided[item.Key].Select(f => $"{f.FileName} (id {f.Id})"))
				+ "; remove the extra ones on the site", item.Key);

		var matched = media.Where(m => byKey.ContainsKey(m.Key)).Select(m => (m.Key, byKey[m.Key])).ToList();
		SiteDb.SaveMedia(connection, matched);
		report.Count("files on the site", matched.Count);

		var missing = media.Where(m => !byKey.ContainsKey(m.Key) && !collided.ContainsKey(m.Key)).ToList();
		report.Count("files not on the site", missing.Count);
		if (missing.Count > 0)
		{
			var stored = storage is null
				? null
				: (await storage.ListAsync(cancellationToken)).Select(SiteDatabase.MediaKey).ToHashSet(StringComparer.Ordinal);
			foreach (var item in missing)
			{
				string reason = stored is null ? "the site did not import it"
					: !stored.Contains(item.Key) ? "not in the storage; run upload"
					: "the site skipped it: no date in the name or the file time, or the conversion failed; see the backend log";
				report.Warn(reason, item.Key);
				output.WriteLine($"warning: {item.Key}: {reason}");
			}
		}

		report.Message = $"{matched.Count} of {media.Count} files are on the site";
		return null;
	}
}
