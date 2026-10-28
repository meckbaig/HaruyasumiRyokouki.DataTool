using HaruyasumiRyokouki.DataTool.Common;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool;

/// <summary>
/// haru-data status: every step with its state, the file counts of the analysis, the errors of
/// the step that failed, and what to run next.
/// </summary>
internal static class StatusView
{
	/// <summary>State of a step for the person.</summary>
	public sealed record Line(Step Step, string State, string Detail);

	public static List<Line> Build(Workspace workspace)
	{
		using var db = WorkDb.Open(workspace);
		RunState.Ensure(db);
		var lines = new List<Line>();
		foreach (var step in Steps.All)
		{
			var report = Report.Read(workspace, step.Name);
			string detail = report is null ? "" : string.Join(", ", report.Counts.Select(c => $"{c.Key} {c.Value}"));
			string state;

			if (step.Kind == StepKind.Local)
			{
				// The files tell more than the last report: a new scan may have added work.
				var (pending, errors) = FileCounts(db, step.Name);
				state = report is null ? "not run"
					: errors > 0 || report.Status != "ok" ? "errors"
					: pending > 0 ? "to run"
					: "done";
				if (pending > 0 || errors > 0)
					detail = $"{pending} files to do, {errors} with errors";
			}
			else if (report is null)
				state = "not run";
			else if (report.Status == "needs_review")
				state = "waiting for you";
			else if (report.Status != "ok")
				state = "errors";
			else
			{
				string? fingerprint = RunState.Fingerprint(db, step.Name);
				string? saved = RunState.Saved(db, step.Name);
				state = fingerprint is null || saved == fingerprint ? "done"
					: saved is null ? "to run"
					: "to run (input changed)";
			}

			if (report?.Warnings > 0)
				detail += $" ({report.Warnings} warnings in reports/{step.Name}.json)";
			lines.Add(new Line(step, state, detail));
		}

		return lines;
	}

	public static int Print(Workspace workspace, TextWriter output)
	{
		var lines = Build(workspace);
		output.WriteLine($"workspace: {workspace.Root}");
		foreach (var line in lines)
			output.WriteLine($"  {line.Step.Name,-16} {line.State,-24} {line.Detail}");

		var failed = lines.FirstOrDefault(l => l.State == "errors");
		var report = failed is null ? null : Report.Read(workspace, failed.Step.Name);
		if (report is not null)
		{
			output.WriteLine();
			output.WriteLine($"{failed!.Step.Name}: {report.Message}");
			foreach (var error in report.Errors.Take(10))
				output.WriteLine($"  {(error.Key is null ? "" : error.Key + ": ")}{error.Message}");
			if (report.Errors.Count > 10)
				output.WriteLine($"  … {report.Errors.Count - 10} more in reports/{failed.Step.Name}.json");
		}

		output.WriteLine();
		var next = lines.FirstOrDefault(l => l.State != "done");
		output.WriteLine(next switch
		{
			null => "Everything is done. Restart the backend if push-embeddings wrote vectors since its last start.",
			{ State: "waiting for you", Step.Kind: StepKind.Review } => "Next: read vocabulary.yaml, then 'haru-data review --approve' and 'haru-data run'.",
			_ => $"Next: {next.Step.Name}. Run 'haru-data run'."
		});
		return ExitCodes.Ok;
	}

	/// <summary>Files still to do and files with errors of an analysis step.</summary>
	private static (long Pending, long Errors) FileCounts(SqliteConnection db, string step)
	{
		string? column = step switch { "frames" => "frame_status", "tag" => "tag_status", "embed" => "embed_status", _ => null };
		if (column is null)
			return (0, 0);

		using var command = db.CreateCommand();
		// Frames exist only for videos; their status is NULL for photos.
		command.CommandText = $"""
			SELECT COALESCE(SUM({column} = 'pending'), 0), COALESCE(SUM({column} = 'error'), 0)
			FROM media WHERE missing = 0 {(step == "frames" ? "AND type = 'video'" : "")}
			""";
		try
		{
			using var reader = command.ExecuteReader();
			reader.Read();
			return (reader.GetInt64(0), reader.GetInt64(1));
		}
		catch (SqliteException ex) when (ex.Message.Contains("no such table"))
		{
			return (0, 0);
		}
	}
}
