using HaruyasumiRyokouki.DataTool.Common;
using Npgsql;

namespace HaruyasumiRyokouki.DataTool.Tags.Site;

/// <summary>
/// The site's Postgres database. The app reads media_files and writes only media_embeddings,
/// which has no API; everything else goes through the backend.
/// </summary>
internal static class SiteDatabase
{
	/// <summary>
	/// Connection string from HARU_CONNECTION; it holds a password, so it is a secret and is never printed.
	/// </summary>
	public static string ConnectionString() => Workspace.RequireSecret("HARU_CONNECTION");

	/// <summary>Host, port and database for printing before writing, without the user or password.</summary>
	public static string Describe(string connectionString)
	{
		var builder = new NpgsqlConnectionStringBuilder(connectionString);
		return $"database: {builder.Host}:{builder.Port}/{builder.Database}";
	}

	public static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
	{
		var connection = new NpgsqlConnection(connectionString);
		try
		{
			await connection.OpenAsync(cancellationToken);
		}
		catch (Exception ex) when (ex is NpgsqlException or ArgumentException)
		{
			await connection.DisposeAsync();
			throw new InvalidOperationException($"Cannot connect to {Describe(connectionString)}: {ex.Message}");
		}

		return connection;
	}

	/// <summary>
	/// Files the site imported from the storage (source Local; YouTube videos have no file here).
	/// </summary>
	public static async Task<List<SiteMediaFile>> ReadMediaFilesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
	{
		var result = new List<SiteMediaFile>();
		// Enums are stored as strings.
		await using var command = new NpgsqlCommand("SELECT id, file_name, type FROM media_files WHERE source = 'Local' ORDER BY id", connection);
		await using var reader = await command.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
			result.Add(new SiteMediaFile(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));

		return result;
	}

	/// <summary>
	/// Site files by key. The site converts photos to another format, so only the name without the
	/// extension stays the same. Two site files with one key are not guessed between: they are
	/// returned as collisions, and the caller reports them.
	/// </summary>
	public static (Dictionary<string, SiteMediaFile> ByKey, List<List<SiteMediaFile>> Collisions) MatchKeys(IEnumerable<SiteMediaFile> files)
	{
		var byKey = new Dictionary<string, SiteMediaFile>(StringComparer.Ordinal);
		var collisions = new List<List<SiteMediaFile>>();
		foreach (var group in files.GroupBy(f => MediaKey(f.FileName), StringComparer.Ordinal))
		{
			if (group.Count() == 1)
				byKey[group.Key] = group.First();
			else
				collisions.Add(group.ToList());
		}

		return (byKey, collisions);
	}

	/// <summary>The key of a file name: without the last extension, lower case (as haru-analysis scan).</summary>
	public static string MediaKey(string fileName) => Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
}

/// <summary>A row of media_files.</summary>
/// <param name="Type"><c>Image</c>, <c>Video</c> or <c>Unknown</c>.</param>
internal sealed record SiteMediaFile(int Id, string FileName, string Type);
