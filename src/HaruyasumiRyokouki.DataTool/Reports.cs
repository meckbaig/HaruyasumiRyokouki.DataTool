using System.Text.Json;
using HaruyasumiRyokouki.DataTool.Common;

namespace HaruyasumiRyokouki.DataTool;

/// <summary>The last report of a step, reports/&lt;step&gt;.json, as both apps write it.</summary>
internal sealed record Report(string Status, string? FinishedAt, List<KeyValuePair<string, long>> Counts,
	List<StepError> Errors, int Warnings, string Message)
{
	/// <summary>The report of the step, or null when the step never ran or the file is broken.</summary>
	public static Report? Read(Workspace workspace, string step)
	{
		string path = Path.Combine(workspace.ReportsDir, step + ".json");
		if (!File.Exists(path))
			return null;

		try
		{
			using var doc = JsonDocument.Parse(File.ReadAllText(path));
			var root = doc.RootElement;
			string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

			var counts = root.TryGetProperty("counts", out var c) && c.ValueKind == JsonValueKind.Object
				? c.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number)
					.Select(p => KeyValuePair.Create(p.Name, p.Value.GetInt64())).ToList()
				: [];
			var errors = root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array
				? e.EnumerateArray().Select(x => new StepError(Text(x, "key"), Text(x, "message") ?? "")).ToList()
				: [];
			int warnings = root.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array ? w.GetArrayLength() : 0;

			return new Report(Text(root, "status") ?? "error", Text(root, "finishedAt"), counts, errors, warnings, Text(root, "message") ?? "");
		}
		catch (JsonException)
		{
			return null;
		}
	}
}
