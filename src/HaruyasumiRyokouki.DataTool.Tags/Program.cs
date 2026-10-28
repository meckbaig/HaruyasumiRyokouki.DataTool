using System.ClientModel;
using System.CommandLine;
using HaruyasumiRyokouki.DataTool.Common;
using HaruyasumiRyokouki.DataTool.Tags;
using HaruyasumiRyokouki.DataTool.Tags.Llm;
using HaruyasumiRyokouki.DataTool.Tags.Vocabulary;
using Microsoft.Data.Sqlite;

// Labels in Russian and Japanese must not turn into question marks in a Windows console.
Console.OutputEncoding = System.Text.Encoding.UTF8;
return Cli.Build().Parse(args).Invoke();

namespace HaruyasumiRyokouki.DataTool.Tags
{
	/// <summary>
	/// haru-tags &lt;step&gt; --workspace &lt;folder&gt;. Every step reads the workspace, writes
	/// reports/&lt;step&gt;.json and exits with 0, 1 or 2 (docs/formats.md).
	/// </summary>
	internal static class Cli
	{
		public static readonly Option<string> WorkspaceOption = new("--workspace")
		{
			Description = "workspace folder with haru-data.toml",
			Required = true,
			Recursive = true
		};

		public static RootCommand Build()
		{
			var root = new RootCommand("Tag vocabulary and import into the HaruyasumiRyokouki site.");
			root.Options.Add(WorkspaceOption);

			var status = new Command("status", "show what haru-analysis found and what this app has done");
			status.SetAction(parse => Run(parse, Status));
			root.Subcommands.Add(status);

			var vocab = new Command("vocab", "build the tag vocabulary from the machine tags with the model in [usage] vocabulary");
			vocab.SetAction((parse, cancellationToken) => RunStepAsync(parse, "vocab", async (workspace, db, report) =>
			{
				var chat = new ChatService(LlmProvider.FromWorkspace(workspace, "vocabulary"), workspace.LlmCacheDir);
				using var api = BackendApi.FromWorkspace(workspace);
				// The backend's AI may be a free tier with a low request rate; raise it for a paid one.
				var translator = new Translator(api, workspace.LlmCacheDir, workspace.GetInt("vocabulary.translate_concurrency") ?? 4, Console.Out);
				await new VocabStep(chat, api, translator, workspace.VocabularyPath, Console.Out).RunAsync(db, report, cancellationToken);
			}));
			root.Subcommands.Add(vocab);

			return root;
		}

		/// <summary>
		/// Runs a step with its report. A problem the user has to fix, a failed model or API call and
		/// Ctrl+C all end in a report with status error and exit code 1; work saved so far stays saved.
		/// </summary>
		private static async Task<int> RunStepAsync(ParseResult parse, string step, Func<Workspace, SqliteConnection, StepReport, Task> run)
		{
			Workspace workspace;
			try
			{
				workspace = Workspace.Open(parse.GetValue(WorkspaceOption)!);
			}
			catch (WorkspaceException ex)
			{
				Console.Error.WriteLine("error: " + ex.Message);
				return ExitCodes.Error;
			}

			var report = new StepReport(step);
			try
			{
				using var db = WorkDb.Open(workspace);
				await run(workspace, db, report);
			}
			catch (OperationCanceledException)
			{
				Console.Error.WriteLine("interrupted, run the step again to continue");
				report.Message = "interrupted";
				return report.Finish(workspace, "error");
			}
			catch (Exception ex) when (ex is WorkspaceException or InvalidOperationException or ClientResultException or HttpRequestException)
			{
				Console.Error.WriteLine("error: " + ex.Message);
				report.Error(ex.Message);
				return report.Finish(workspace);
			}

			Console.WriteLine($"{step}: " + string.Join(", ", report.Counts.Select(c => $"{c.Key} {c.Value}")));
			return report.Finish(workspace);
		}

		/// <summary>Opens the workspace and turns problems the user has to fix into one line and exit code 1.</summary>
		private static int Run(ParseResult parse, Func<Workspace, int> step)
		{
			try
			{
				return step(Workspace.Open(parse.GetValue(WorkspaceOption)!));
			}
			catch (WorkspaceException ex)
			{
				Console.Error.WriteLine("error: " + ex.Message);
				return ExitCodes.Error;
			}
		}

		private static int Status(Workspace workspace)
		{
			using var db = WorkDb.Open(workspace);
			AnalysisData.Require(db);

			foreach (var (name, count) in AnalysisData.Summary(db))
				Console.WriteLine($"{name}: {count}");

			return ExitCodes.Ok;
		}
	}
}
