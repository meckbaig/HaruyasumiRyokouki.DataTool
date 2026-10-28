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
	public const int SchemaVersion = 3;

	private const string Schema = """
		CREATE TABLE IF NOT EXISTS vocab_tags (
		  tag         TEXT PRIMARY KEY,
		  kind        TEXT NOT NULL,
		  photos      INTEGER NOT NULL,
		  action      TEXT NOT NULL CHECK (action IN ('keep', 'drop')),
		  reason      TEXT,
		  slug        TEXT,
		  label_en    TEXT,
		  label_ru    TEXT,
		  label_ja    TEXT,
		  existing_id INTEGER
		);
		CREATE TABLE IF NOT EXISTS vocab_aliases (
		  tag      TEXT NOT NULL REFERENCES vocab_tags(tag),
		  language TEXT NOT NULL,
		  text     TEXT NOT NULL,
		  PRIMARY KEY (tag, language, text)
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
		CREATE TABLE IF NOT EXISTS vocab_approval (
		  id          INTEGER PRIMARY KEY CHECK (id = 1),
		  file_sha256 TEXT NOT NULL,
		  approved_at TEXT NOT NULL
		);
		""";

	public static void Ensure(SqliteConnection connection) => WorkDb.EnsureOwnSchema(connection, Owner, SchemaVersion, Schema);

	/// <summary>Replaces the vocabulary with a new one in one transaction.</summary>
	public static void Save(SqliteConnection connection, IEnumerable<VocabTag> tags, IEnumerable<Checks.Problem> problems)
	{
		using var transaction = connection.BeginTransaction();
		// A new vocabulary needs a new review.
		Execute(connection, "DELETE FROM vocab_approval; DELETE FROM vocab_aliases; DELETE FROM vocab_sources; DELETE FROM vocab_tags; DELETE FROM vocab_problems;");

		foreach (var tag in tags)
		{
			Execute(connection, """
				INSERT INTO vocab_tags(tag, kind, photos, action, reason, slug, label_en, label_ru, label_ja, existing_id)
				VALUES ($tag, $kind, $photos, $action, $reason, $slug, $en, $ru, $ja, $existing)
				""",
				("$tag", tag.Name), ("$kind", tag.Kind), ("$photos", tag.Keys.Count), ("$action", tag.Keep ? "keep" : "drop"),
				("$reason", tag.Reason), ("$slug", tag.Keep ? tag.Slug : null), ("$en", tag.Labels.GetValueOrDefault("en")),
				("$ru", tag.Labels.GetValueOrDefault("ru")), ("$ja", tag.Labels.GetValueOrDefault("ja")), ("$existing", tag.ExistingId));
			foreach (var (language, list) in tag.Aliases)
				foreach (string text in list)
					Execute(connection, "INSERT OR IGNORE INTO vocab_aliases(tag, language, text) VALUES ($tag, $language, $text)",
						("$tag", tag.Name), ("$language", language), ("$text", text));
			foreach (string raw in tag.RawTags)
				Execute(connection, "INSERT INTO vocab_sources(raw_tag, tag) VALUES ($raw, $tag)", ("$raw", raw), ("$tag", tag.Name));
		}

		foreach (var problem in problems)
			Execute(connection, "INSERT INTO vocab_problems(kind, subject, message) VALUES ($kind, $subject, $message)",
				("$kind", problem.Kind), ("$subject", problem.Tag), ("$message", problem.Message));

		transaction.Commit();
	}

	/// <summary>
	/// Replaces the model's vocabulary with the reviewed file and records its hash. Tags the file
	/// leaves out are gone, and their sources go nowhere: those photos simply do not get that tag.
	/// </summary>
	/// <param name="keysBySource">Files of every raw tag, to count the photos of a tag.</param>
	public static void Approve(SqliteConnection connection, IReadOnlyList<VocabularyCheck.Tag> tags,
		IReadOnlyDictionary<string, HashSet<string>> keysBySource, string fileSha256)
	{
		// Site ids of tags matched by the vocab step, found again by slug or English label.
		var existingIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		using (var read = connection.CreateCommand())
		{
			read.CommandText = "SELECT slug, label_en, existing_id FROM vocab_tags WHERE existing_id IS NOT NULL";
			using var reader = read.ExecuteReader();
			while (reader.Read())
			{
				if (!reader.IsDBNull(0)) existingIds.TryAdd(reader.GetString(0), reader.GetInt64(2));
				if (!reader.IsDBNull(1)) existingIds.TryAdd(reader.GetString(1), reader.GetInt64(2));
			}
		}

		long? ExistingId(VocabularyCheck.Tag tag)
			=> tag.Existing && (existingIds.TryGetValue(tag.Slug, out long id) || existingIds.TryGetValue(tag.Labels["en"], out id)) ? id : null;

		var unknown = tags.Where(t => t.Existing && ExistingId(t) is null).Select(t => $"'{t.Slug}' (line {t.Line})").ToList();
		if (unknown.Count > 0)
			throw new InvalidOperationException(
				$"marked \"existing: true\", but not matched to a site tag by the vocab step: {string.Join(", ", unknown)}. Set existing to false or run vocab again.");

		using var transaction = connection.BeginTransaction();
		Execute(connection, "DELETE FROM vocab_approval; DELETE FROM vocab_aliases; DELETE FROM vocab_sources; DELETE FROM vocab_tags;");

		foreach (var tag in tags)
		{
			string name = tag.Labels["en"];
			long? existing = ExistingId(tag);
			int photos = tag.Sources.SelectMany(s => keysBySource.GetValueOrDefault(s) ?? []).Distinct().Count();

			Execute(connection, """
				INSERT INTO vocab_tags(tag, kind, photos, action, slug, label_en, label_ru, label_ja, existing_id)
				VALUES ($tag, $kind, $photos, 'keep', $slug, $en, $ru, $ja, $existing)
				""",
				("$tag", name), ("$kind", tag.Kind), ("$photos", photos), ("$slug", tag.Slug),
				("$en", name), ("$ru", tag.Labels["ru"]), ("$ja", tag.Labels["ja"]), ("$existing", existing));
			foreach (var (language, list) in tag.Aliases)
				foreach (string text in list)
					Execute(connection, "INSERT OR IGNORE INTO vocab_aliases(tag, language, text) VALUES ($tag, $language, $text)",
						("$tag", name), ("$language", language), ("$text", text));
			foreach (string source in tag.Sources)
				Execute(connection, "INSERT INTO vocab_sources(raw_tag, tag) VALUES ($raw, $tag)", ("$raw", source), ("$tag", name));
		}

		Execute(connection, "INSERT INTO vocab_approval(id, file_sha256, approved_at) VALUES (1, $hash, $at)",
			("$hash", fileSha256), ("$at", DateTimeOffset.UtcNow.ToString("O")));
		transaction.Commit();
	}

	/// <summary>Hash of the approved vocabulary.yaml, or null when the vocabulary was not approved.</summary>
	public static string? ApprovedSha256(SqliteConnection connection)
	{
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT file_sha256 FROM vocab_approval WHERE id = 1";
		return command.ExecuteScalar() as string;
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

	/// <summary>Slug for the site, from the English name; empty when the name has no Latin letters.</summary>
	public string Slug => TagNormalizer.Slugify(Name);

	/// <summary>Main label per language: en, ru, ja.</summary>
	public Dictionary<string, string> Labels { get; } = [];

	/// <summary>Search aliases per language.</summary>
	public Dictionary<string, List<string>> Aliases { get; } = [];

	/// <summary>Id of the same tag already on the site; such a tag is not created again.</summary>
	public int? ExistingId { get; set; }
}
