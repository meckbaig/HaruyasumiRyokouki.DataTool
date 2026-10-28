using HaruyasumiRyokouki.DataTool.Common;

namespace HaruyasumiRyokouki.DataTool;

/// <summary>
/// haru-data run: the steps in their order, each as a process. Local steps start every time and
/// skip what is done. The cloud step and the steps that write to the site start only when their
/// input changed since they last finished, so a second run of a finished cycle does nothing. The
/// run stops at the vocabulary review, at a declined question and at any error.
/// </summary>
internal sealed class Runner(Workspace workspace, IStepLauncher launcher, TextWriter output)
{
	/// <summary>Pass --yes to the steps and do not ask before the cloud step.</summary>
	public bool Yes { get; init; }

	/// <summary>Approve vocabulary.yaml and translations.yaml as they are instead of stopping for the review.</summary>
	public bool NoReview { get; init; }

	/// <summary>Asks before the cloud step; replaced in tests.</summary>
	public Func<string, bool> Confirm { get; init; } = action => Confirmation.Ask(action, [], false);

	/// <summary>Runs the steps that are not done, or only <paramref name="only"/>, which then runs anyway.</summary>
	public int Run(Step? only = null)
	{
		bool vectorsWritten = false;
		foreach (var step in only is null ? Steps.All : [only])
		{
			string? fingerprint;
			using (var db = WorkDb.Open(workspace))
			{
				RunState.Ensure(db);
				fingerprint = RunState.Fingerprint(db, step.Name);
				if (only is null && fingerprint is not null && IsDone(db, step, fingerprint))
				{
					output.WriteLine($"== {step.Name}: up to date");
					continue;
				}
			}

			output.WriteLine($"== {step.Name}: {step.Description}");
			int code = RunStep(step);
			if (code == ExitCodes.Ok)
			{
				vectorsWritten |= step.Name == "push-embeddings";
				if (fingerprint is not null)
				{
					using var db = WorkDb.Open(workspace);
					RunState.Save(db, step.Name, fingerprint);
				}
				continue;
			}

			output.WriteLine();
			output.WriteLine(code == ExitCodes.NeedsReview ? Waiting(step) : Failed(step));
			return code == ExitCodes.NeedsReview ? ExitCodes.NeedsReview : ExitCodes.Error;
		}

		if (only is null)
		{
			output.WriteLine();
			output.WriteLine("All steps are done.");
			if (vectorsWritten)
				output.WriteLine("Restart the backend so the similarity search loads the new vectors, then look through the new files in the admin panel.");
		}
		return ExitCodes.Ok;
	}

	private bool IsDone(Microsoft.Data.Sqlite.SqliteConnection db, Step step, string fingerprint)
		=> Report.Read(workspace, step.Name)?.Status == "ok" && RunState.Saved(db, step.Name) == fingerprint;

	private int RunStep(Step step)
	{
		var arguments = new List<string>();
		switch (step.Kind)
		{
			case StepKind.Cloud:
				int estimate = launcher.Run(step.App, step.Name, ["--estimate"]);
				if (estimate != ExitCodes.Ok)
					return estimate;
				if (!Yes && !Confirm($"Run {step.Name} and send these requests?"))
					return ExitCodes.NeedsReview;
				break;
			case StepKind.Review when NoReview:
				arguments.Add("--approve");
				break;
			case StepKind.Site when Yes:
				arguments.Add("--yes");
				break;
		}

		int code = launcher.Run(step.App, step.Name, arguments);
		// The translations wait for a review; --no-review approves them as they are.
		if (code == ExitCodes.NeedsReview && step.Kind == StepKind.OnChange && NoReview)
			code = launcher.Run(App.Tags, "review", ["--approve"]);
		return code;
	}

	private static string Waiting(Step step) => step.Kind is StepKind.Review or StepKind.OnChange
		? """
			Your check is needed. Run 'haru-data review': it opens a page in the browser where you look
			through the tags, fix what is marked and press Approve. Then run 'haru-data run' again.
			"""
		: step.Kind == StepKind.Cloud
			? $"{step.Name} was not confirmed, nothing was sent. Run 'haru-data run' again to continue, with --yes where nobody can answer."
			: $"{step.Name} was not confirmed, nothing was written. Run 'haru-data run' again when the target is right.";

	private static string Failed(Step step)
		=> $"{step.Name} failed: see the messages above and reports/{step.Name}.json. Finished work is kept; fix the problem and run 'haru-data run' again.";
}
