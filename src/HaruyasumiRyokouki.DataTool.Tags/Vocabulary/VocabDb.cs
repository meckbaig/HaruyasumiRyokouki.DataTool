using System.Security.Cryptography;
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
	public const int SchemaVersion = 4;

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
		CREATE TABLE IF NOT EXISTS vocab_exclusions (
		  tag TEXT NOT NULL REFERENCES vocab_tags(tag),
		  key TEXT NOT NULL,
		  PRIMARY KEY (tag, key)
		);
		CREATE TABLE IF NOT EXISTS vocab_translations (
		  id                INTEGER PRIMARY KEY CHECK (id = 1),
		  vocabulary_sha256 TEXT NOT NULL,
		  file_sha256       TEXT,
		  approved_at       TEXT
		);
		""";

	public static void Ensure(SqliteConnection connection) => WorkDb.EnsureOwnSchema(connection, Owner, SchemaVersion, Schema);

	/// <summary>Replaces the vocabulary with a new one in one transaction.</summary>
	public static void Save(SqliteConnection connection, IEnumerable<VocabTag> tags, IEnumerable<Checks.Problem> problems)
	{
		using var transaction = connection.BeginTransaction();
		// A new vocabulary needs a new review.
		Execute(connection, "DELETE FROM vocab_translations; DELETE FROM vocab_approval; DELETE FROM vocab_exclusions; DELETE FROM vocab_aliases; DELETE FROM vocab_sources; DELETE FROM vocab_tags; DELETE FROM vocab_problems;");

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
		// A new vocabulary needs new translations.
		Execute(connection, "DELETE FROM vocab_translations; DELETE FROM vocab_approval; DELETE FROM vocab_exclusions; DELETE FROM vocab_aliases; DELETE FROM vocab_sources; DELETE FROM vocab_tags;");

		foreach (var tag in tags)
		{
			string name = tag.Labels["en"];
			long? existing = ExistingId(tag);
			int photos = tag.Sources.SelectMany(s => keysBySource.GetValueOrDefault(s) ?? []).Distinct().Except(tag.Excluded).Count();

			Execute(connection, """
				INSERT INTO vocab_tags(tag, kind, photos, action, slug, label_en, label_ru, label_ja, existing_id)
				VALUES ($tag, $kind, $photos, 'keep', $slug, $en, $ru, $ja, $existing)
				""",
				("$tag", name), ("$kind", tag.Kind), ("$photos", photos), ("$slug", tag.Slug),
				("$en", name), ("$ru", tag.Labels.GetValueOrDefault("ru")), ("$ja", tag.Labels.GetValueOrDefault("ja")), ("$existing", existing));
			foreach (var (language, list) in tag.Aliases)
				foreach (string text in list)
					Execute(connection, "INSERT OR IGNORE INTO vocab_aliases(tag, language, text) VALUES ($tag, $language, $text)",
						("$tag", name), ("$language", language), ("$text", text));
			foreach (string source in tag.Sources)
				Execute(connection, "INSERT INTO vocab_sources(raw_tag, tag) VALUES ($raw, $tag)", ("$raw", source), ("$tag", name));
			foreach (string key in tag.Excluded.Distinct())
				Execute(connection, "INSERT INTO vocab_exclusions(tag, key) VALUES ($tag, $key)", ("$tag", name), ("$key", key));
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

	/// <summary>
	/// Stops a step that writes tags to the site unless vocabulary.yaml is approved as it is now.
	/// </summary>
	/// <returns>The hash of the approved file.</returns>
	/// <exception cref="WorkspaceException">No approval, or the file changed after it.</exception>
	public static string RequireApproved(SqliteConnection connection, string vocabularyPath)
	{
		Ensure(connection);
		string? approved = ApprovedSha256(connection);
		if (approved is null)
			throw new WorkspaceException("The vocabulary is not approved. Run haru-tags review, then haru-tags review --approve.");

		if (FileSha256(vocabularyPath) != approved)
			throw new WorkspaceException($"{vocabularyPath} changed after the approval. Run haru-data review --approve again.");
		return approved;
	}

	/// <summary>SHA-256 of a file, or "" when it does not exist.</summary>
	public static string FileSha256(string path)
		=> File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() : "";

	/// <summary>Translations made for an approved vocabulary, and the hash of translations.yaml once approved.</summary>
	public sealed record TranslationState(string VocabularySha256, string? FileSha256);

	public static TranslationState? ReadTranslationState(SqliteConnection connection)
	{
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT vocabulary_sha256, file_sha256 FROM vocab_translations WHERE id = 1";
		using var reader = command.ExecuteReader();
		return reader.Read() ? new TranslationState(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
	}

	/// <summary>
	/// Stores the labels and aliases of the new tags and the state of their review: made for
	/// <paramref name="vocabularySha256"/>, approved when <paramref name="fileSha256"/> is set.
	/// </summary>
	public static void SaveTranslations(SqliteConnection connection, IEnumerable<VocabularyCheck.Tag> tags,
		string vocabularySha256, string? fileSha256)
	{
		using var transaction = connection.BeginTransaction();
		foreach (var tag in tags)
		{
			string? name;
			using (var find = connection.CreateCommand())
			{
				find.CommandText = "SELECT tag FROM vocab_tags WHERE slug = $slug";
				find.Parameters.AddWithValue("$slug", tag.Slug);
				name = find.ExecuteScalar() as string;
			}
			if (name is null)
				continue;

			Execute(connection, "UPDATE vocab_tags SET label_en = $en, label_ru = $ru, label_ja = $ja WHERE tag = $tag",
				("$tag", name), ("$en", tag.Labels.GetValueOrDefault("en")), ("$ru", tag.Labels.GetValueOrDefault("ru")), ("$ja", tag.Labels.GetValueOrDefault("ja")));
			Execute(connection, "DELETE FROM vocab_aliases WHERE tag = $tag", ("$tag", name));
			foreach (var (language, list) in tag.Aliases)
				foreach (string text in list)
					Execute(connection, "INSERT OR IGNORE INTO vocab_aliases(tag, language, text) VALUES ($tag, $language, $text)",
						("$tag", name), ("$language", language), ("$text", text));
		}

		Execute(connection, """
			INSERT INTO vocab_translations(id, vocabulary_sha256, file_sha256, approved_at) VALUES (1, $vocabulary, $file, $at)
			ON CONFLICT(id) DO UPDATE SET vocabulary_sha256 = excluded.vocabulary_sha256, file_sha256 = excluded.file_sha256, approved_at = excluded.approved_at
			""",
			("$vocabulary", vocabularySha256), ("$file", fileSha256), ("$at", fileSha256 is null ? null : DateTimeOffset.UtcNow.ToString("O")));
		transaction.Commit();
	}

	/// <summary>
	/// Stops push-tags unless the vocabulary and the translations of its new tags are approved as
	/// the files are now.
	/// </summary>
	/// <exception cref="WorkspaceException">Something is not approved or changed after the approval.</exception>
	public static void RequireTranslationsApproved(SqliteConnection connection, string vocabularyPath, string translationsPath)
	{
		string vocabulary = RequireApproved(connection, vocabularyPath);
		var state = ReadTranslationState(connection);
		if (state is null || state.VocabularySha256 != vocabulary)
			throw new WorkspaceException("The new tags are not translated yet. Run haru-data run (or haru-tags translate).");
		if (state.FileSha256 is null)
			throw new WorkspaceException($"{translationsPath} waits for review. Fix it, then run haru-data review --approve.");
		if (FileSha256(translationsPath) != state.FileSha256)
			throw new WorkspaceException($"{translationsPath} changed after the approval. Run haru-data review --approve again.");
	}

	/// <summary>The approved tags with their labels, aliases and the files that get them.</summary>
	public static List<ApprovedTag> ReadApproved(SqliteConnection connection)
	{
		var tags = new Dictionary<string, ApprovedTag>(StringComparer.Ordinal);
		using (var command = connection.CreateCommand())
		{
			command.CommandText = """
				SELECT tag, slug, label_en, label_ru, label_ja, existing_id FROM vocab_tags
				WHERE action = 'keep' ORDER BY slug
				""";
			using var reader = command.ExecuteReader();
			while (reader.Read())
			{
				string name = reader.GetString(0);
				tags[name] = new ApprovedTag(reader.GetString(1),
					Labels(reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)),
					[], reader.IsDBNull(5) ? null : (int)reader.GetInt64(5), new HashSet<string>(StringComparer.Ordinal));
			}
		}

		using (var command = connection.CreateCommand())
		{
			command.CommandText = "SELECT tag, language, text FROM vocab_aliases ORDER BY tag, language, text";
			using var reader = command.ExecuteReader();
			while (reader.Read())
				if (tags.TryGetValue(reader.GetString(0), out var tag))
					tag.Aliases.Add(new TagText(reader.GetString(1), reader.GetString(2)));
		}

		// Files of a tag: every file with one of its sources that is still in the source folder.
		using (var command = connection.CreateCommand())
		{
			command.CommandText = """
				SELECT s.tag, r.key FROM vocab_sources s
				JOIN raw_tags r ON r.tag = s.raw_tag
				JOIN media m ON m.key = r.key AND m.missing = 0
				""";
			using var reader = command.ExecuteReader();
			while (reader.Read())
				if (tags.TryGetValue(reader.GetString(0), out var tag))
					tag.Keys.Add(reader.GetString(1));
		}

		// Photos a person took the tag off in the review.
		using (var command = connection.CreateCommand())
		{
			command.CommandText = "SELECT tag, key FROM vocab_exclusions";
			using var reader = command.ExecuteReader();
			while (reader.Read())
				if (tags.TryGetValue(reader.GetString(0), out var tag))
					tag.Keys.Remove(reader.GetString(1));
		}

		return tags.Values.ToList();
	}

	/// <summary>A tag of the approved vocabulary for the review page: names, sources and photos taken off it.</summary>
	public sealed record ApprovedTagInfo(string Slug, string En, bool Existing, List<string> Sources, List<string> Excluded);

	public static List<ApprovedTagInfo> ReadApprovedTags(SqliteConnection connection)
	{
		var tags = new Dictionary<string, ApprovedTagInfo>(StringComparer.Ordinal);
		using (var command = connection.CreateCommand())
		{
			command.CommandText = "SELECT tag, slug, label_en, existing_id FROM vocab_tags WHERE action = 'keep' ORDER BY label_en";
			using var reader = command.ExecuteReader();
			while (reader.Read())
				tags[reader.GetString(0)] = new ApprovedTagInfo(reader.GetString(1), reader.GetString(2), !reader.IsDBNull(3), [], []);
		}

		foreach (var (sql, list) in new (string, Func<ApprovedTagInfo, List<string>>)[]
		{
			("SELECT tag, raw_tag FROM vocab_sources ORDER BY raw_tag", t => t.Sources),
			("SELECT tag, key FROM vocab_exclusions ORDER BY key", t => t.Excluded)
		})
		{
			using var command = connection.CreateCommand();
			command.CommandText = sql;
			using var reader = command.ExecuteReader();
			while (reader.Read())
				if (tags.TryGetValue(reader.GetString(0), out var tag))
					list(tag).Add(reader.GetString(1));
		}

		return tags.Values.ToList();
	}

	private static Dictionary<string, string> Labels(string en, string? ru, string? ja)
	{
		var labels = new Dictionary<string, string> { ["en"] = en };
		if (ru is not null) labels["ru"] = ru;
		if (ja is not null) labels["ja"] = ja;
		return labels;
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

/// <summary>A tag of the approved vocabulary, as the steps that write to the site see it.</summary>
/// <param name="Labels">Main label per language: en, and ru and ja once translated (tags the site has already are not).</param>
/// <param name="ExistingId">Id of the tag on the site, when the vocab step matched one.</param>
/// <param name="Keys">Files that get the tag: those with any of its sources, except the ones excluded in the review.</param>
internal sealed record ApprovedTag(string Slug, Dictionary<string, string> Labels, List<TagText> Aliases, int? ExistingId, HashSet<string> Keys);

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
