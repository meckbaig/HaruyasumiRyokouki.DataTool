using System.CommandLine;
using HaruyasumiRyokouki.DataTool;
using HaruyasumiRyokouki.DataTool.Common;

// Tags in Russian and Japanese must not turn into question marks in a Windows console.
Console.OutputEncoding = System.Text.Encoding.UTF8;
return Cli.Build().Parse(args).Invoke();

namespace HaruyasumiRyokouki.DataTool
{
	/// <summary>
	/// haru-data: init, run, status, doctor, review. The workspace is the current folder unless
	/// --workspace names another one.
	/// </summary>
	internal static class Cli
	{
		private static readonly Option<string> WorkspaceOption = new("--workspace")
		{
			Description = "workspace folder with haru-data.toml; the current folder by default",
			DefaultValueFactory = _ => ".",
			Recursive = true
		};

		public static RootCommand Build()
		{
			var root = new RootCommand("Fills the HaruyasumiRyokouki site with the photos and videos of a trip: tags in three languages and image vectors.");
			root.Options.Add(WorkspaceOption);

			var folder = new Argument<string>("folder") { Description = "the new workspace folder" };
			var source = new Option<string>("--source") { Description = "folder with the photos and videos of the trip", Required = true };
			var init = new Command("init", "create a workspace with haru-data.toml and .env, then check the environment");
			init.Arguments.Add(folder);
			init.Options.Add(source);
			init.SetAction(parse => Guard(() =>
			{
				var workspace = Init.Create(parse.GetValue(folder)!, parse.GetValue(source)!);
				Console.WriteLine($"Created {Path.Combine(workspace.Root, Workspace.ConfigName)} and .env.");
				Console.WriteLine("Fill in [backend], [storage] and the secrets in .env, then run 'haru-data run' in that folder.");
				Console.WriteLine();
				// The settings are not filled in yet, so missing things are expected here.
				new Doctor(RepoLayout.Find(), Console.Out).RunAsync(workspace).GetAwaiter().GetResult();
				return ExitCodes.Ok;
			}));
			root.Subcommands.Add(init);

			var step = new Argument<string?>("step")
			{
				Description = "run only this step, even if it is done: " + string.Join(", ", Steps.All.Select(s => s.Name)),
				Arity = ArgumentArity.ZeroOrOne
			};
			step.Validators.Add(result =>
			{
				string? name = result.GetValueOrDefault<string?>();
				if (name is not null && Steps.Find(name) is null)
					result.AddError($"unknown step '{name}'; steps: {string.Join(", ", Steps.All.Select(s => s.Name))}");
			});
			var yes = new Option<bool>("--yes") { Description = "do not ask before writing to the site or calling the cloud model; targets are still printed" };
			var noReview = new Option<bool>("--no-review") { Description = "approve vocabulary.yaml as the model wrote it instead of stopping for the review" };
			var run = new Command("run", "run the steps that are not done, in order; stops at the review, at questions and at errors");
			run.Arguments.Add(step);
			run.Options.Add(yes);
			run.Options.Add(noReview);
			run.SetAction(parse => Guard(() =>
			{
				var workspace = OpenWorkspace(parse);
				var runner = new Runner(workspace, new ProcessLauncher(RepoLayout.Find(), workspace.Root), Console.Out)
				{
					Yes = parse.GetValue(yes),
					NoReview = parse.GetValue(noReview)
				};
				string? only = parse.GetValue(step);
				return runner.Run(only is null ? null : Steps.Find(only));
			}));
			root.Subcommands.Add(run);

			var status = new Command("status", "where the cycle is, the errors of a failed step and what to run next");
			status.SetAction(parse => Guard(() => StatusView.Print(OpenWorkspace(parse), Console.Out)));
			root.Subcommands.Add(status);

			var doctor = new Command("doctor", "check the programs, models and servers the steps need");
			doctor.SetAction(parse => Guard(() =>
			{
				var path = parse.GetValue(WorkspaceOption)!;
				var workspace = File.Exists(Path.Combine(path, Workspace.ConfigName)) ? Workspace.Open(path) : null;
				return new Doctor(RepoLayout.Find(), Console.Out).RunAsync(workspace).GetAwaiter().GetResult();
			}));
			root.Subcommands.Add(doctor);

			var check = new Option<bool>("--check") { Description = "check the edited vocabulary.yaml, with line numbers" };
			var approve = new Option<bool>("--approve") { Description = "check vocabulary.yaml and make it the vocabulary written to the site" };
			var review = new Command("review", "the vocabulary review of haru-tags: summary, --check or --approve");
			review.Options.Add(check);
			review.Options.Add(approve);
			review.SetAction(parse => Guard(() =>
			{
				var workspace = OpenWorkspace(parse);
				var arguments = new List<string>();
				if (parse.GetValue(check)) arguments.Add("--check");
				if (parse.GetValue(approve)) arguments.Add("--approve");
				return new ProcessLauncher(RepoLayout.Find(), workspace.Root).Run(App.Tags, "review", arguments);
			}));
			root.Subcommands.Add(review);

			return root;
		}

		private static Workspace OpenWorkspace(ParseResult parse) => Workspace.Open(parse.GetValue(WorkspaceOption)!);

		/// <summary>A problem the person has to fix becomes one line and exit code 1.</summary>
		private static int Guard(Func<int> action)
		{
			try
			{
				return action();
			}
			catch (WorkspaceException ex)
			{
				Console.Error.WriteLine("error: " + ex.Message);
				return ExitCodes.Error;
			}
		}
	}
}
