using HaruyasumiRyokouki.DataTool.Common;
using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;

namespace HaruyasumiRyokouki.DataTool.Tags.Site;

/// <summary>
/// Step push-embeddings: writes the SigLIP vectors of photos and videos into media_embeddings,
/// the only table this app writes directly (it has no API and can be rebuilt from work.sqlite at
/// any time). One transaction: only the rows being written are replaced, the others stay. The
/// site loads its similarity index at start, so it sees the new vectors after a restart.
/// </summary>
internal sealed class PushEmbeddingsStep(string connectionString, bool dryRun, Confirm confirm, TextWriter output)
{
	/// <summary>Fixed, not a setting: the site reads this table only.</summary>
	private const string Table = "media_embeddings";

	/// <summary>The site's similarity index takes vectors of exactly this length.</summary>
	public const int Dim = 768;

	public async Task<string?> RunAsync(SqliteConnection connection, StepReport report, CancellationToken cancellationToken)
	{
		AnalysisData.Require(connection);
		var models = AnalysisData.ReadVectorModels(connection);
		if (models.Count > 1)
			throw new InvalidOperationException(
				$"work.sqlite has vectors of several models ({string.Join(", ", models)}); they cannot be compared. Run haru-analysis embed --reset.");
		var vectors = AnalysisData.ReadVectors(connection);
		if (vectors.Count == 0)
			throw new WorkspaceException("No vectors. Run haru-analysis embed.");
		Validate(vectors);
		string model = models[0];
		var media = AnalysisData.ReadMedia(connection).Select(m => m.Key).ToHashSet(StringComparer.Ordinal);

		// Ids come from the database itself rather than from the sync step: a file deleted on the
		// site since then would break the whole transaction.
		await using var db = await SiteDatabase.OpenAsync(connectionString, cancellationToken);
		await EnsureTableAsync(db, cancellationToken);
		var (byKey, collisions) = SiteDatabase.MatchKeys(await SiteDatabase.ReadMediaFilesAsync(db, cancellationToken));
		var collided = collisions.Select(g => SiteDatabase.MediaKey(g[0].FileName)).ToHashSet(StringComparer.Ordinal);

		var rows = new List<(int Id, float[] Vector)>();
		foreach (var (key, vector) in vectors.Where(v => media.Contains(v.Key)).OrderBy(v => v.Key, StringComparer.Ordinal))
		{
			if (collided.Contains(key))
				report.Error("the site has several files with this key; see the sync report", key);
			else if (byKey.TryGetValue(key, out var file))
				rows.Add((file.Id, vector));
			else
				report.Count("vectors without a file on the site");
		}
		report.Count("vectors to write", rows.Count);
		output.WriteLine($"{rows.Count} vectors of {model} match site files, {report.Counts.GetValueOrDefault("vectors without a file on the site")} do not");

		if (rows.Count == 0)
		{
			report.Message = "no vector matches a site file; run upload and sync first";
			return "error";
		}

		if (dryRun)
		{
			report.Message = "--dry-run: nothing written";
			return null;
		}

		if (!confirm($"Write {rows.Count} vectors into {Table}, replacing the rows of these files:", [SiteDatabase.Describe(connectionString)]))
		{
			report.Message = "not confirmed, nothing written";
			return "needs_review";
		}

		await using (var transaction = await db.BeginTransactionAsync(cancellationToken))
		{
			await using (var delete = new NpgsqlCommand($"DELETE FROM {Table} WHERE media_file_id = ANY(@ids)", db, transaction))
			{
				delete.Parameters.AddWithValue("ids", rows.Select(r => r.Id).ToArray());
				report.Count("rows replaced", await delete.ExecuteNonQueryAsync(cancellationToken));
			}

			await using (var writer = await db.BeginBinaryImportAsync($"COPY {Table} (media_file_id, vector, model) FROM STDIN (FORMAT BINARY)", cancellationToken))
			{
				foreach (var (id, vector) in rows)
				{
					await writer.StartRowAsync(cancellationToken);
					await writer.WriteAsync(id, NpgsqlDbType.Integer, cancellationToken);
					await writer.WriteAsync(vector, NpgsqlDbType.Array | NpgsqlDbType.Real, cancellationToken);
					await writer.WriteAsync(model, NpgsqlDbType.Text, cancellationToken);
				}
				await writer.CompleteAsync(cancellationToken);
			}

			await transaction.CommitAsync(cancellationToken);
		}
		report.Count("written", rows.Count);

		// The number that matters most: such files never show up among similar ones, and nobody notices.
		await using (var command = new NpgsqlCommand($"""
			SELECT m.file_name FROM media_files m
			WHERE m.source = 'Local' AND m.type <> 'Unknown'
			  AND NOT EXISTS (SELECT 1 FROM {Table} e WHERE e.media_file_id = m.id)
			ORDER BY m.file_name
			""", db))
		await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
		{
			var without = new List<string>();
			while (await reader.ReadAsync(cancellationToken))
				without.Add(reader.GetString(0));

			report.Count("site files without a vector", without.Count);
			foreach (string name in without)
				report.Warn("no vector: it never shows up among similar files", SiteDatabase.MediaKey(name));
			if (without.Count > 0)
				output.WriteLine($"warning: {without.Count} site files have no vector, for example {string.Join(", ", without.Take(5))}");
		}

		report.Message = $"wrote {rows.Count} vectors; restart the backend so the similarity index loads them";
		output.WriteLine(report.Message);
		return null;
	}

	/// <summary>Length 768 and unit norm: the similarity index relies on both, and a wrong vector fails silently there.</summary>
	private static void Validate(Dictionary<string, float[]> vectors)
	{
		foreach (var (key, vector) in vectors)
		{
			if (vector.Length != Dim)
				throw new InvalidOperationException($"{key}: vector of length {vector.Length}, the site needs {Dim}");

			double norm = Math.Sqrt(vector.Sum(x => (double)x * x));
			if (Math.Abs(norm - 1.0) > 0.01)
				throw new InvalidOperationException($"{key}: vector is not normalized (length {norm:F4}); run haru-analysis embed --reset");
		}
	}

	private static async Task EnsureTableAsync(NpgsqlConnection db, CancellationToken cancellationToken)
	{
		await using var command = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", db);
		command.Parameters.AddWithValue("name", Table);
		if (await command.ExecuteScalarAsync(cancellationToken) is not true)
			throw new InvalidOperationException($"The database has no {Table} table: the backend there is older than the similarity search.");
	}
}
