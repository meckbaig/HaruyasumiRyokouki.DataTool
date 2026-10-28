using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaruyasumiRyokouki.DataTool.Common;

/// <summary>Exit codes shared by every step of every app.</summary>
public static class ExitCodes
{
	/// <summary>The step finished without errors.</summary>
	public const int Ok = 0;

	/// <summary>The step failed or recorded errors; later steps must not run.</summary>
	public const int Error = 1;

	/// <summary>The step waits for a person (review, confirmation).</summary>
	public const int NeedsReview = 2;
}

/// <summary>
/// Counts and errors of one step, written to <c>reports/&lt;step&gt;.json</c> in the same
/// format as the Python app (<c>docs/formats.md</c>). The orchestrator reads it to decide what runs next.
/// </summary>
public sealed class StepReport(string step)
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

	/// <summary>Step name, also the report file name.</summary>
	public string Step { get; } = step;

	/// <summary>Step-specific counters, written in insertion order.</summary>
	public Dictionary<string, long> Counts { get; } = [];

	/// <summary>Errors recorded so far; any error makes the status <c>error</c>.</summary>
	public List<StepError> Errors { get; } = [];

	/// <summary>Things the person should look at that do not stop the next steps.</summary>
	public List<StepError> Warnings { get; } = [];

	/// <summary>One line for the person reading the report.</summary>
	public string Message { get; set; } = "";

	/// <summary>Adds to a counter.</summary>
	public void Count(string name, long n = 1) => Counts[name] = Counts.GetValueOrDefault(name) + n;

	/// <summary>Records an error, optionally about one item (file key, tag slug).</summary>
	public void Error(string message, string? key = null) => Errors.Add(new StepError(key, message));

	/// <summary>Records a warning, optionally about one item.</summary>
	public void Warn(string message, string? key = null) => Warnings.Add(new StepError(key, message));

	/// <summary>Writes the report and returns the exit code for its status.</summary>
	/// <param name="status"><c>ok</c>, <c>error</c> or <c>needs_review</c>; by default <c>error</c> if any error was recorded.</param>
	public int Finish(Workspace workspace, string? status = null)
	{
		status ??= Errors.Count > 0 ? "error" : "ok";
		var report = new
		{
			step = Step,
			status,
			startedAt = _startedAt.ToString("yyyy-MM-ddTHH:mm:sszzz"),
			finishedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz"),
			counts = Counts,
			errors = Errors,
			warnings = Warnings.Count > 0 ? Warnings : null,
			message = Message
		};

		Directory.CreateDirectory(workspace.ReportsDir);
		File.WriteAllText(Path.Combine(workspace.ReportsDir, Step + ".json"), JsonSerializer.Serialize(report, JsonOptions));

		return status switch
		{
			"ok" => ExitCodes.Ok,
			"needs_review" => ExitCodes.NeedsReview,
			_ => ExitCodes.Error
		};
	}
}

/// <summary>An error or a warning in a step report.</summary>
/// <param name="Key">The item it is about, or null for the whole step.</param>
/// <param name="Message">What went wrong.</param>
public sealed record StepError(string? Key, string Message);
