using HaruyasumiRyokouki.DataTool.Common;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Tags;

/// <summary>
/// Read access to the tables haru-analysis owns in work.sqlite (docs/formats.md).
/// This app never writes them.
/// </summary>
internal static class AnalysisData
{
	/// <summary>The analysis schema version this code reads.</summary>
	public const int SchemaVersion = 3;

	/// <summary>Checks that haru-analysis has run and its tables are the expected version.</summary>
	public static void Require(SqliteConnection connection)
		=> WorkDb.RequireSchema(connection, "analysis", SchemaVersion, "haru-analysis scan");

	/// <summary>Tags and names the vision model found, for files that are still in the source folder.</summary>
	public static List<RawTag> ReadRawTags(SqliteConnection connection)
	{
		var result = new List<RawTag>();
		using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT r.key, r.tag, r.kind, r.evidence
			FROM raw_tags r JOIN media m ON m.key = r.key
			WHERE m.missing = 0
			ORDER BY r.key, r.tag
			""";

		using var reader = command.ExecuteReader();
		while (reader.Read())
			result.Add(new RawTag(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));

		return result;
	}

	/// <summary>Files that are still in the source folder.</summary>
	public static List<MediaItem> ReadMedia(SqliteConnection connection)
	{
		var result = new List<MediaItem>();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT key, path, type FROM media WHERE missing = 0 ORDER BY key";

		using var reader = command.ExecuteReader();
		while (reader.Read())
			result.Add(new MediaItem(reader.GetString(0), reader.GetString(1), reader.GetString(2)));

		return result;
	}

	/// <summary>Models the vectors were made with; more than one means they cannot be compared.</summary>
	public static List<string> ReadVectorModels(SqliteConnection connection)
	{
		var result = new List<string>();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT DISTINCT model FROM embeddings ORDER BY model";

		using var reader = command.ExecuteReader();
		while (reader.Read())
			result.Add(reader.GetString(0));

		return result;
	}

	/// <summary>Image vectors by file key.</summary>
	/// <exception cref="InvalidDataException">A blob does not match its stated dimension.</exception>
	public static Dictionary<string, float[]> ReadVectors(SqliteConnection connection)
	{
		var result = new Dictionary<string, float[]>(StringComparer.Ordinal);
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT key, dim, vector FROM embeddings";

		using var reader = command.ExecuteReader();
		while (reader.Read())
		{
			string key = reader.GetString(0);
			int dim = reader.GetInt32(1);
			byte[] blob = (byte[])reader["vector"];
			if (blob.Length != dim * sizeof(float))
				throw new InvalidDataException($"{key}: vector of dim {dim} has {blob.Length} bytes");

			// Little-endian float32 on both sides; x86 and ARM need no conversion.
			var vector = new float[dim];
			Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
			result[key] = vector;
		}

		return result;
	}

	/// <summary>Counts per step status, for <c>haru-tags status</c>.</summary>
	public static Dictionary<string, long> Summary(SqliteConnection connection)
	{
		var result = new Dictionary<string, long>();
		using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT 'files', COUNT(*) FROM media WHERE missing = 0
			UNION ALL SELECT 'files tagged', COUNT(*) FROM media WHERE missing = 0 AND tag_status = 'done'
			UNION ALL SELECT 'files with tag errors', COUNT(*) FROM media WHERE missing = 0 AND tag_status = 'error'
			UNION ALL SELECT 'files with vectors', COUNT(*) FROM media WHERE missing = 0 AND embed_status = 'done'
			UNION ALL SELECT 'distinct tags', COUNT(DISTINCT tag) FROM raw_tags WHERE kind = 'tag'
			UNION ALL SELECT 'distinct names', COUNT(DISTINCT tag) FROM raw_tags WHERE kind != 'tag'
			""";

		using var reader = command.ExecuteReader();
		while (reader.Read())
			result[reader.GetString(0)] = reader.GetInt64(1);

		return result;
	}
}

/// <summary>A file of the source folder.</summary>
/// <param name="Key">File name without the last extension, lower case.</param>
/// <param name="Path">Full path of the file.</param>
/// <param name="Type"><c>image</c> or <c>video</c>.</param>
internal sealed record MediaItem(string Key, string Path, string Type);

/// <summary>One row of raw_tags.</summary>
/// <param name="Key">File key.</param>
/// <param name="Tag">Tag or name as cleaned by the analysis step.</param>
/// <param name="Kind"><c>tag</c>, or the kind of a name: landmark, place, brand, franchise, character, name.</param>
/// <param name="Evidence">What the model saw for a name; null for tags.</param>
internal sealed record RawTag(string Key, string Tag, string Kind, string? Evidence);
