using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Common;

/// <summary>
/// The shared <c>work.sqlite</c>. Every table has one owner app that creates and writes it;
/// other apps only read it after checking the owner's schema version (<c>docs/formats.md</c>).
/// </summary>
public static class WorkDb
{
	/// <summary>Opens (and creates if needed) the workspace database.</summary>
	public static SqliteConnection Open(Workspace workspace)
	{
		var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = workspace.DbPath }.ToString());
		connection.Open();
		Execute(connection, """
			PRAGMA foreign_keys = ON;
			CREATE TABLE IF NOT EXISTS schema_versions (
			  owner   TEXT PRIMARY KEY,
			  version INTEGER NOT NULL
			);
			""");
		return connection;
	}

	/// <summary>
	/// Creates the owner's tables on first use and refuses a database made by a different
	/// version of the owner. There are no migrations: a new workspace is created instead.
	/// </summary>
	/// <param name="schema">DDL with <c>CREATE TABLE IF NOT EXISTS</c> statements.</param>
	/// <exception cref="WorkspaceException">The stored version differs.</exception>
	public static void EnsureOwnSchema(SqliteConnection connection, string owner, int version, string schema)
	{
		int? stored = GetVersion(connection, owner);
		if (stored is not null && stored != version)
			throw new WorkspaceException(
				$"work.sqlite has {owner} schema version {stored}, this version of the tool needs {version}. Create a new workspace.");

		Execute(connection, schema);
		if (stored is null)
		{
			using var insert = connection.CreateCommand();
			insert.CommandText = "INSERT INTO schema_versions(owner, version) VALUES ($owner, $version)";
			insert.Parameters.AddWithValue("$owner", owner);
			insert.Parameters.AddWithValue("$version", version);
			insert.ExecuteNonQuery();
		}
	}

	/// <summary>Checks that another app's tables exist in the version this code was written for.</summary>
	/// <param name="producer">The step that creates the tables, for the error message.</param>
	/// <exception cref="WorkspaceException">The tables are missing or have another version.</exception>
	public static void RequireSchema(SqliteConnection connection, string owner, int version, string producer)
	{
		int? stored = GetVersion(connection, owner);
		if (stored is null)
			throw new WorkspaceException($"work.sqlite has no {owner} data yet. Run {producer} first.");
		if (stored != version)
			throw new WorkspaceException(
				$"work.sqlite has {owner} schema version {stored}, this tool reads version {version}. Update the tools or create a new workspace.");
	}

	private static int? GetVersion(SqliteConnection connection, string owner)
	{
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT version FROM schema_versions WHERE owner = $owner";
		command.Parameters.AddWithValue("$owner", owner);
		return command.ExecuteScalar() is long value ? (int)value : null;
	}

	private static void Execute(SqliteConnection connection, string sql)
	{
		using var command = connection.CreateCommand();
		command.CommandText = sql;
		command.ExecuteNonQuery();
	}
}
