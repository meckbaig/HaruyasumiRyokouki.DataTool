using HaruyasumiRyokouki.DataTool.Common;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Tags.Tests;

public sealed class AnalysisDataTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("haru-tags-").FullName;
	private readonly SqliteConnection _db;

	public AnalysisDataTests()
	{
		File.WriteAllText(Path.Combine(_root, Workspace.ConfigName), "");
		_db = WorkDb.Open(Workspace.Open(_root));
	}

	public void Dispose()
	{
		_db.Dispose();
		SqliteConnection.ClearAllPools();
		Directory.Delete(_root, recursive: true);
	}

	// The tables as haru-analysis creates them (src/analysis/haru_analysis/db.py, schema version 3).
	private void CreateAnalysisTables(int version = AnalysisData.SchemaVersion)
	{
		using var command = _db.CreateCommand();
		command.CommandText = $"""
			CREATE TABLE media (key TEXT PRIMARY KEY, path TEXT NOT NULL, type TEXT NOT NULL, size INTEGER NOT NULL,
			  mtime_ns INTEGER NOT NULL, missing INTEGER NOT NULL DEFAULT 0, frame_status TEXT, frame_error TEXT,
			  tag_status TEXT NOT NULL DEFAULT 'pending', tag_error TEXT, tag_description TEXT,
			  embed_status TEXT NOT NULL DEFAULT 'pending', embed_error TEXT);
			CREATE TABLE raw_tags (key TEXT NOT NULL, tag TEXT NOT NULL, kind TEXT NOT NULL DEFAULT 'tag', evidence TEXT,
			  PRIMARY KEY (key, tag));
			CREATE TABLE embeddings (key TEXT PRIMARY KEY, model TEXT NOT NULL, dim INTEGER NOT NULL, vector BLOB NOT NULL);
			INSERT INTO schema_versions(owner, version) VALUES ('analysis', {version});
			INSERT INTO media(key, path, type, size, mtime_ns, tag_status, embed_status) VALUES
			  ('a', 'a.jpg', 'image', 1, 1, 'done', 'done'),
			  ('b', 'b.jpg', 'image', 1, 1, 'error', 'pending');
			INSERT INTO media(key, path, type, size, mtime_ns, missing) VALUES ('gone', 'gone.jpg', 'image', 1, 1, 1);
			INSERT INTO raw_tags(key, tag, kind, evidence) VALUES
			  ('a', 'torii', 'tag', NULL),
			  ('a', 'itsukushima shrine', 'landmark', 'the torii in the water'),
			  ('gone', 'ramen', 'tag', NULL);
			""";
		command.ExecuteNonQuery();
	}

	[Fact]
	public void Requires_analysis_tables_of_the_expected_version()
	{
		Assert.Throws<WorkspaceException>(() => AnalysisData.Require(_db));
		CreateAnalysisTables(version: 2);
		Assert.Throws<WorkspaceException>(() => AnalysisData.Require(_db));
	}

	[Fact]
	public void Reads_tags_and_names_of_present_files_only()
	{
		CreateAnalysisTables();
		AnalysisData.Require(_db);

		var tags = AnalysisData.ReadRawTags(_db);

		Assert.Equal(
			[new RawTag("a", "itsukushima shrine", "landmark", "the torii in the water"), new RawTag("a", "torii", "tag", null)],
			tags);
	}

	[Fact]
	public void Reads_vectors_and_rejects_a_broken_blob()
	{
		CreateAnalysisTables();
		float[] vector = [0.6f, 0.8f];
		var blob = new byte[8];
		Buffer.BlockCopy(vector, 0, blob, 0, 8);

		using (var insert = _db.CreateCommand())
		{
			insert.CommandText = "INSERT INTO embeddings(key, model, dim, vector) VALUES ('a', 'm', 2, $v)";
			insert.Parameters.AddWithValue("$v", blob);
			insert.ExecuteNonQuery();
		}
		Assert.Equal(vector, AnalysisData.ReadVectors(_db)["a"]);

		using (var broken = _db.CreateCommand())
		{
			broken.CommandText = "UPDATE embeddings SET dim = 3";
			broken.ExecuteNonQuery();
		}
		Assert.Throws<InvalidDataException>(() => AnalysisData.ReadVectors(_db));
	}

	[Fact]
	public void Summary_counts_present_files()
	{
		CreateAnalysisTables();
		var summary = AnalysisData.Summary(_db);
		Assert.Equal(2, summary["files"]);
		Assert.Equal(1, summary["files tagged"]);
		Assert.Equal(1, summary["files with tag errors"]);
		Assert.Equal(1, summary["files with vectors"]);
		Assert.Equal(2, summary["distinct tags"]);
		Assert.Equal(1, summary["distinct names"]);
	}
}
