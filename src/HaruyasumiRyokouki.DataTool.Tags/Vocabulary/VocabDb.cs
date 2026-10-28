using HaruyasumiRyokouki.DataTool.Common;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

/// <summary>
/// Tables haru-tags owns in work.sqlite (docs/formats.md). The vocabulary step rewrites them on
/// every run; the model answers come from the cache, so a rerun is cheap.
/// </summary>
internal static class VocabDb
{
	public const string Owner = "tags";
	public const int SchemaVersion = 1;

	private const string Schema = """
		CREATE TABLE IF NOT EXISTS vocab_tags (
		  tag     TEXT PRIMARY KEY,
		  kind    TEXT NOT NULL,
		  photos  INTEGER NOT NULL,
		  action  TEXT NOT NULL CHECK (action IN ('keep', 'drop')),
		  reason  TEXT
		);
		CREATE TABLE IF NOT EXISTS vocab_sources (
		  raw_tag TEXT PRIMARY KEY,
		  tag     TEXT NOT NULL REFERENCES vocab_tags(tag)
		);
		CREATE TABLE IF NOT EXISTS vocab_problems (
		  id      INTEGER PRIMARY KEY,
		  kind    TEXT NOT NULL,
		  subject TEXT,
		  message TEXT NOT NULL
		);
		""";

	public static void Ensure(SqliteConnection connection) => WorkDb.EnsureOwnSchema(connection, Owner, SchemaVersion, Schema);

	/// <summary>Replaces the vocabulary with a new one in one transaction.</summary>
	public static void Save(SqliteConnection connection, IEnumerable<VocabTag> tags, IEnumerable<(string Kind, string? Subject, string Message)> problems)
	{
		using var transaction = connection.BeginTransaction();
		Execute(connection, "DELETE FROM vocab_sources; DELETE FROM vocab_tags; DELETE FROM vocab_problems;");

		foreach (var tag in tags)
		{
			Execute(connection, "INSERT INTO vocab_tags(tag, kind, photos, action, reason) VALUES ($tag, $kind, $photos, $action, $reason)",
				("$tag", tag.Name), ("$kind", tag.Kind), ("$photos", tag.Keys.Count), ("$action", tag.Keep ? "keep" : "drop"), ("$reason", tag.Reason));
			foreach (string raw in tag.RawTags)
				Execute(connection, "INSERT INTO vocab_sources(raw_tag, tag) VALUES ($raw, $tag)", ("$raw", raw), ("$tag", tag.Name));
		}

		foreach (var (kind, subject, message) in problems)
			Execute(connection, "INSERT INTO vocab_problems(kind, subject, message) VALUES ($kind, $subject, $message)",
				("$kind", kind), ("$subject", subject), ("$message", message));

		transaction.Commit();
	}

	private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
	{
		using var command = connection.CreateCommand();
		command.CommandText = sql;
		foreach (var (name, value) in parameters)
			command.Parameters.AddWithValue(name, value ?? DBNull.Value);
		command.ExecuteNonQuery();
	}
}

/// <summary>A tag of the vocabulary: the entries merged into it and the keep-or-drop decision.</summary>
internal sealed class VocabTag(string name)
{
	/// <summary>English display name chosen by the model; the base of the slug.</summary>
	public string Name { get; } = name;

	public string Kind { get; set; } = "tag";
	public List<VocabEntry> Entries { get; } = [];
	public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
	public HashSet<string> RawTags { get; } = new(StringComparer.Ordinal);
	public bool Keep { get; set; } = true;
	public string? Reason { get; set; }
}
