using HaruyasumiRyokouki.DataTool.Common;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Tests;

public sealed class RunnerTests : IDisposable
{
	/// <summary>Records the calls and writes the report a real step would write.</summary>
	private sealed class FakeLauncher(Workspace workspace) : IStepLauncher
	{
		public List<string> Calls { get; } = [];
		public Dictionary<string, int> Codes { get; } = [];
		public Action<string>? OnRun { get; set; }

		public int Run(App app, string step, IReadOnlyList<string> arguments)
		{
			Calls.Add(string.Join(" ", new[] { step }.Concat(arguments)));
			if (arguments.Contains("--estimate"))
				return ExitCodes.Ok;

			OnRun?.Invoke(step);
			int code = Codes.GetValueOrDefault(step);
			var report = new StepReport(step);
			report.Finish(workspace, code switch { 0 => "ok", 2 => "needs_review", _ => "error" });
			return code;
		}
	}

	private readonly string _root = Directory.CreateTempSubdirectory("haru-run-").FullName;
	private readonly Workspace _workspace;
	private readonly FakeLauncher _launcher;

	public RunnerTests()
	{
		File.WriteAllText(Path.Combine(_root, Workspace.ConfigName), "");
		_workspace = Workspace.Open(_root);
		_launcher = new FakeLauncher(_workspace);
		using var db = WorkDb.Open(_workspace);
		Execute(db, """
			CREATE TABLE media (key TEXT PRIMARY KEY, type TEXT NOT NULL DEFAULT 'image', missing INTEGER NOT NULL DEFAULT 0,
			  frame_status TEXT, tag_status TEXT NOT NULL DEFAULT 'done', embed_status TEXT NOT NULL DEFAULT 'done');
			CREATE TABLE raw_tags (key TEXT NOT NULL, tag TEXT NOT NULL);
			INSERT INTO media(key) VALUES ('a'), ('b');
			INSERT INTO raw_tags VALUES ('a', 'torii'), ('b', 'ramen');
			""");
	}

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		Directory.Delete(_root, recursive: true);
	}

	private static void Execute(SqliteConnection db, string sql)
	{
		using var command = db.CreateCommand();
		command.CommandText = sql;
		command.ExecuteNonQuery();
	}

	private Runner Runner(bool yes = false, bool noReview = false, bool confirm = true)
		=> new(_workspace, _launcher, TextWriter.Null) { Yes = yes, NoReview = noReview, Confirm = _ => confirm };

	[Fact]
	public void Stops_at_the_review_after_the_estimate_and_the_question()
	{
		_launcher.Codes["review"] = ExitCodes.NeedsReview;

		Assert.Equal(ExitCodes.NeedsReview, Runner().Run());

		Assert.Equal(["scan", "frames", "tag", "embed", "vocab --estimate", "vocab", "review"], _launcher.Calls);
	}

	[Fact]
	public void A_declined_cloud_step_stops_without_running_it()
	{
		Assert.Equal(ExitCodes.NeedsReview, Runner(confirm: false).Run());
		Assert.Equal("vocab --estimate", _launcher.Calls[^1]);
	}

	[Fact]
	public void A_finished_cycle_runs_only_the_local_steps_and_the_review_again()
	{
		Assert.Equal(ExitCodes.Ok, Runner(yes: true, noReview: true).Run());
		Assert.Contains("review --approve", _launcher.Calls);
		Assert.Contains("push-embeddings --yes", _launcher.Calls);
		// --yes skips the question, the estimate is still printed.
		Assert.Contains("vocab --estimate", _launcher.Calls);

		_launcher.Calls.Clear();
		Runner(yes: true, noReview: true).Run();
		Assert.Equal(["scan", "frames", "tag", "embed", "review --approve"], _launcher.Calls);
	}

	[Fact]
	public void New_machine_tags_run_the_vocabulary_and_the_site_steps_again()
	{
		Runner(yes: true).Run();
		using (var db = WorkDb.Open(_workspace))
			Execute(db, "INSERT INTO raw_tags VALUES ('b', 'noodles')");

		_launcher.Calls.Clear();
		Runner(yes: true).Run();

		Assert.Contains("vocab", _launcher.Calls);
		// The vocabulary approval did not change in this fake, so push-tags stays done.
		Assert.DoesNotContain("push-tags --yes", _launcher.Calls);
	}

	[Fact]
	public void An_error_stops_the_run_and_the_step_runs_again_next_time()
	{
		_launcher.Codes["upload"] = ExitCodes.Error;
		Assert.Equal(ExitCodes.Error, Runner(yes: true).Run());
		Assert.Equal("upload --yes", _launcher.Calls[^1]);

		_launcher.Codes["upload"] = ExitCodes.Ok;
		_launcher.Calls.Clear();
		Assert.Equal(ExitCodes.Ok, Runner(yes: true).Run());
		Assert.Contains("upload --yes", _launcher.Calls);
		Assert.DoesNotContain("push-tags --yes", _launcher.Calls);
	}

	[Fact]
	public void One_step_runs_even_when_it_is_done()
	{
		Runner(yes: true).Run();
		_launcher.Calls.Clear();

		Assert.Equal(ExitCodes.Ok, Runner(yes: true).Run(Steps.Find("sync")));
		Assert.Equal(["sync --yes"], _launcher.Calls);
	}

	[Fact]
	public void Status_shows_what_is_done_and_what_changed()
	{
		Runner(yes: true).Run();
		using (var db = WorkDb.Open(_workspace))
			Execute(db, "INSERT INTO media(key) VALUES ('c')");

		var states = StatusView.Build(_workspace).ToDictionary(l => l.Step.Name, l => l.State);

		Assert.Equal("done", states["vocab"]);
		Assert.Equal("to run (input changed)", states["upload"]);
		Assert.Equal("done", states["assign"]);
	}
}
