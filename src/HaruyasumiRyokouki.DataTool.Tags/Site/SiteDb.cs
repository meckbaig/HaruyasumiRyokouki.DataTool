using HaruyasumiRyokouki.DataTool.Common;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Tags.Site;

/// <summary>
/// Tables haru-tags keeps about the site in work.sqlite (docs/formats.md): the ids of the tags
/// and of the media files, which the steps that write to the site look up by slug and by key.
/// </summary>
internal static class SiteDb
{
	public const string Owner = "site";
	public const int SchemaVersion = 1;

	private const string Schema = """
		CREATE TABLE IF NOT EXISTS site_tags (
		  slug    TEXT PRIMARY KEY,
		  tag_id  INTEGER NOT NULL,
		  created INTEGER NOT NULL
		);
		CREATE TABLE IF NOT EXISTS site_media (
		  key           TEXT PRIMARY KEY,
		  media_file_id INTEGER NOT NULL,
		  file_name     TEXT NOT NULL,
		  type          TEXT NOT NULL
		);
		""";

	public static void Ensure(SqliteConnection connection) => WorkDb.EnsureOwnSchema(connection, Owner, SchemaVersion, Schema);

	/// <summary>Records the site id of a tag; <paramref name="created"/> when this app created it.</summary>
	public static void SaveTag(SqliteConnection connection, string slug, int tagId, bool created)
	{
		using var command = connection.CreateCommand();
		command.CommandText = """
			INSERT INTO site_tags(slug, tag_id, created) VALUES ($slug, $id, $created)
			ON CONFLICT(slug) DO UPDATE SET tag_id = excluded.tag_id,
			  created = CASE WHEN site_tags.tag_id = excluded.tag_id THEN site_tags.created ELSE excluded.created END
			""";
		command.Parameters.AddWithValue("$slug", slug);
		command.Parameters.AddWithValue("$id", tagId);
		command.Parameters.AddWithValue("$created", created ? 1 : 0);
		command.ExecuteNonQuery();
	}

	/// <summary>Site tag ids by slug.</summary>
	public static Dictionary<string, int> ReadTags(SqliteConnection connection)
	{
		var result = new Dictionary<string, int>(StringComparer.Ordinal);
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT slug, tag_id FROM site_tags";
		using var reader = command.ExecuteReader();
		while (reader.Read())
			result[reader.GetString(0)] = (int)reader.GetInt64(1);

		return result;
	}

	/// <summary>Replaces the media ids with the ones just read from the site's database.</summary>
	public static void SaveMedia(SqliteConnection connection, IEnumerable<(string Key, SiteMediaFile File)> media)
	{
		using var transaction = connection.BeginTransaction();
		using (var delete = connection.CreateCommand())
		{
			delete.CommandText = "DELETE FROM site_media";
			delete.ExecuteNonQuery();
		}

		foreach (var (key, file) in media)
		{
			using var command = connection.CreateCommand();
			command.CommandText = "INSERT INTO site_media(key, media_file_id, file_name, type) VALUES ($key, $id, $name, $type)";
			command.Parameters.AddWithValue("$key", key);
			command.Parameters.AddWithValue("$id", file.Id);
			command.Parameters.AddWithValue("$name", file.FileName);
			command.Parameters.AddWithValue("$type", file.Type);
			command.ExecuteNonQuery();
		}

		transaction.Commit();
	}

	/// <summary>Site media ids by file key, as the sync step saved them.</summary>
	public static Dictionary<string, int> ReadMedia(SqliteConnection connection)
	{
		var result = new Dictionary<string, int>(StringComparer.Ordinal);
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT key, media_file_id FROM site_media";
		using var reader = command.ExecuteReader();
		while (reader.Read())
			result[reader.GetString(0)] = (int)reader.GetInt64(1);

		return result;
	}
}
