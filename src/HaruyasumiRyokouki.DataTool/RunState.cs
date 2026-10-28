using System.Security.Cryptography;
using System.Text;
using HaruyasumiRyokouki.DataTool.Common;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool;

/// <summary>
/// What the orchestrator remembers in work.sqlite (owner <c>run</c>, docs/formats.md): the
/// fingerprint of the input of every step that only runs when its input changed. The fingerprints
/// are computed from the tables of the other apps, which are only read.
/// </summary>
internal static class RunState
{
	public const string Owner = "run";
	public const int SchemaVersion = 1;

	private const string Schema = """
		CREATE TABLE IF NOT EXISTS run_steps (
		  step        TEXT PRIMARY KEY,
		  fingerprint TEXT NOT NULL,
		  finished_at TEXT NOT NULL
		);
		""";

	public static void Ensure(SqliteConnection connection) => WorkDb.EnsureOwnSchema(connection, Owner, SchemaVersion, Schema);

	/// <summary>The fingerprint the step last finished with, or null.</summary>
	public static string? Saved(SqliteConnection connection, string step)
	{
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT fingerprint FROM run_steps WHERE step = $step";
		command.Parameters.AddWithValue("$step", step);
		return command.ExecuteScalar() as string;
	}

	public static void Save(SqliteConnection connection, string step, string fingerprint)
	{
		using var command = connection.CreateCommand();
		command.CommandText = """
			INSERT INTO run_steps(step, fingerprint, finished_at) VALUES ($step, $fingerprint, $at)
			ON CONFLICT(step) DO UPDATE SET fingerprint = excluded.fingerprint, finished_at = excluded.finished_at
			""";
		command.Parameters.AddWithValue("$step", step);
		command.Parameters.AddWithValue("$fingerprint", fingerprint);
		command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
		command.ExecuteNonQuery();
	}

	/// <summary>
	/// The input of a step that runs only when it changed, or null for a step that runs every time.
	/// Tables that do not exist yet count as empty.
	/// </summary>
	public static string? Fingerprint(SqliteConnection connection, string step) => step switch
	{
		// New machine tags need a new vocabulary.
		"vocab" => Hash(connection, "SELECT DISTINCT r.tag FROM raw_tags r JOIN media m ON m.key = r.key WHERE m.missing = 0 ORDER BY 1"),
		// A new approved vocabulary.
		"translate" => Approval(connection),
		// A new vocabulary or new approved translations.
		"push-tags" => Approval(connection) + Hash(connection, "SELECT vocabulary_sha256, file_sha256 FROM vocab_translations"),
		// New files of the source folder.
		"upload" or "sync" => Hash(connection, "SELECT key FROM media WHERE missing = 0 ORDER BY 1"),
		// A new vocabulary or new files on the site.
		"assign" => Approval(connection) + SiteMedia(connection),
		"push-embeddings" => Hash(connection, "SELECT key, model, vector FROM embeddings ORDER BY 1") + SiteMedia(connection),
		_ => null
	};

	private static string Approval(SqliteConnection connection) => Hash(connection, "SELECT file_sha256 FROM vocab_approval");

	private static string SiteMedia(SqliteConnection connection) => Hash(connection, "SELECT key, media_file_id FROM site_media ORDER BY 1");

	/// <summary>SHA-256 of the rows of a query, shortened; "-" when a table of the query does not exist yet.</summary>
	private static string Hash(SqliteConnection connection, string sql)
	{
		using var command = connection.CreateCommand();
		command.CommandText = sql;
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		try
		{
			using var reader = command.ExecuteReader();
			while (reader.Read())
			{
				for (int i = 0; i < reader.FieldCount; i++)
					hash.AppendData(reader.GetValue(i) is byte[] bytes ? bytes : Encoding.UTF8.GetBytes(reader.GetValue(i) + "\t"));
				hash.AppendData("\n"u8);
			}
		}
		catch (SqliteException ex) when (ex.Message.Contains("no such table"))
		{
			return "-";
		}

		return Convert.ToHexString(hash.GetHashAndReset())[..16].ToLowerInvariant();
	}
}
