using HaruyasumiRyokouki.DataTool.Common;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Tags.Site;

/// <summary>
/// Step upload: copies the files of the source folder into the storage the site imports from.
/// A file whose key the storage already has, with any extension, is skipped: after a sync the
/// site keeps photos in its own format (.webp) and deletes the original. The original
/// modification time goes along, since the site dates a file without a date in its name by it.
/// </summary>
internal sealed class UploadStep(IMediaStorage storage, int concurrency, Confirm confirm, TextWriter output)
{
	/// <summary>Attempts of one file when the network or the server fails.</summary>
	public int Attempts { get; init; } = 3;

	public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

	public async Task<string?> RunAsync(SqliteConnection connection, StepReport report, CancellationToken cancellationToken)
	{
		AnalysisData.Require(connection);
		var media = AnalysisData.ReadMedia(connection);
		var stored = (await storage.ListAsync(cancellationToken)).Select(SiteDatabase.MediaKey).ToHashSet(StringComparer.Ordinal);

		var toUpload = media.Where(m => !stored.Contains(m.Key)).ToList();
		report.Count("already in the storage", media.Count - toUpload.Count);
		report.Count("uploaded", 0);
		if (toUpload.Count == 0)
		{
			report.Message = "the storage has every file";
			return null;
		}

		long bytes = toUpload.Sum(m => new FileInfo(m.Path).Length);
		if (!confirm($"Upload {toUpload.Count} files ({bytes / 1024.0 / 1024.0:F0} MB):", [storage.Description]))
		{
			report.Message = "not confirmed, nothing uploaded";
			return "needs_review";
		}

		int done = 0;
		var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = cancellationToken };
		await Parallel.ForEachAsync(toUpload, options, async (item, token) =>
		{
			string? error = await UploadOneAsync(item, token);
			lock (report)
			{
				if (error is null)
					report.Count("uploaded");
				else
					report.Error(error, item.Key);

				done++;
				if (done % 25 == 0 || done == toUpload.Count)
					output.WriteLine($"{done}/{toUpload.Count}");
			}
		});

		report.Message = $"uploaded {report.Counts["uploaded"]} of {toUpload.Count} files";
		return null;
	}

	/// <summary>Returns null on success, otherwise the error after the last attempt.</summary>
	private async Task<string?> UploadOneAsync(MediaItem item, CancellationToken cancellationToken)
	{
		if (!File.Exists(item.Path))
			return $"{item.Path} is gone; run haru-analysis scan";

		DateTime modified = File.GetLastWriteTimeUtc(item.Path);
		for (int attempt = 1; ; attempt++)
		{
			try
			{
				await storage.UploadAsync(item.Path, Path.GetFileName(item.Path), modified, cancellationToken);
				return null;
			}
			catch (Exception ex) when (ex is HttpRequestException or IOException
				|| ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
			{
				if (attempt >= Attempts)
					return ex.Message;
				await Task.Delay(RetryDelay * attempt, cancellationToken);
			}
		}
	}
}
