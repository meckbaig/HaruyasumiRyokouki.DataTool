using System.CommandLine;
using HaruyasumiRyokouki.DataTool.Common;
using HaruyasumiRyokouki.DataTool.Tags;

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

			return root;
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
