using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Common.Tests;

public sealed class WorkDbAndReportTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("haru-db-").FullName;
	private readonly Workspace _workspace;

	public WorkDbAndReportTests()
	{
		File.WriteAllText(Path.Combine(_root, Workspace.ConfigName), "");
		_workspace = Workspace.Open(_root);
	}

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		Directory.Delete(_root, recursive: true);
	}

	private const string Schema = "CREATE TABLE IF NOT EXISTS things (id INTEGER PRIMARY KEY);";

	[Fact]
	public void Own_schema_is_created_once_and_its_version_recorded()
	{
		using (var db = WorkDb.Open(_workspace))
			WorkDb.EnsureOwnSchema(db, "tags", 1, Schema);

		using (var db = WorkDb.Open(_workspace))
		{
			WorkDb.EnsureOwnSchema(db, "tags", 1, Schema);
			WorkDb.RequireSchema(db, "tags", 1, "nothing");
		}
	}

	[Fact]
	public void Other_version_of_own_schema_is_refused()
	{
		using var db = WorkDb.Open(_workspace);
		WorkDb.EnsureOwnSchema(db, "tags", 1, Schema);

		var ex = Assert.Throws<WorkspaceException>(() => WorkDb.EnsureOwnSchema(db, "tags", 2, Schema));
		Assert.Contains("new workspace", ex.Message);
	}

	[Fact]
	public void Reading_tables_of_another_owner_checks_they_exist_in_the_expected_version()
	{
		using var db = WorkDb.Open(_workspace);

		var missing = Assert.Throws<WorkspaceException>(() => WorkDb.RequireSchema(db, "analysis", 3, "haru-analysis scan"));
		Assert.Contains("haru-analysis scan", missing.Message);

		WorkDb.EnsureOwnSchema(db, "analysis", 2, Schema);
		Assert.Throws<WorkspaceException>(() => WorkDb.RequireSchema(db, "analysis", 3, "haru-analysis scan"));
	}

	[Fact]
	public void Report_has_the_shared_format_and_exit_codes()
	{
		var report = new StepReport("push-tags");
		report.Count("created", 3);
		report.Count("created");
		report.Message = "done";
		Assert.Equal(ExitCodes.Ok, report.Finish(_workspace));

		using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_workspace.ReportsDir, "push-tags.json"))))
		{
			var json = doc.RootElement;
			Assert.Equal("push-tags", json.GetProperty("step").GetString());
			Assert.Equal("ok", json.GetProperty("status").GetString());
			Assert.Equal(4, json.GetProperty("counts").GetProperty("created").GetInt64());
			Assert.Equal(0, json.GetProperty("errors").GetArrayLength());
			Assert.True(DateTimeOffset.TryParse(json.GetProperty("startedAt").GetString(), out _));
			Assert.Equal("done", json.GetProperty("message").GetString());
		}

		report.Error("HTTP 400", "temple");
		Assert.Equal(ExitCodes.Error, report.Finish(_workspace));
		using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_workspace.ReportsDir, "push-tags.json"))))
		{
			var error = doc.RootElement.GetProperty("errors")[0];
			Assert.Equal("temple", error.GetProperty("key").GetString());
			Assert.Equal("HTTP 400", error.GetProperty("message").GetString());
		}

		Assert.Equal(ExitCodes.NeedsReview, new StepReport("review").Finish(_workspace, "needs_review"));
	}
}
