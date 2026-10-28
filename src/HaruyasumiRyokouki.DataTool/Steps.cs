namespace HaruyasumiRyokouki.DataTool;

/// <summary>The app that runs a step.</summary>
internal enum App { Analysis, Tags }

/// <summary>How the orchestrator treats a step.</summary>
internal enum StepKind
{
	/// <summary>Local work that skips what is done; cheap to start every time.</summary>
	Local,

	/// <summary>Paid calls to a cloud model; runs only when its input changed, after an estimate.</summary>
	Cloud,

	/// <summary>The vocabulary review: stops the run until a person approves.</summary>
	Review,

	/// <summary>Writes to the site; runs only when its input changed and asks before writing.</summary>
	Site
}

/// <summary>A step of the cycle.</summary>
internal sealed record Step(string Name, App App, StepKind Kind, string Description);

internal static class Steps
{
	/// <summary>All steps in the order of the cycle.</summary>
	public static readonly IReadOnlyList<Step> All =
	[
		new("scan", App.Analysis, StepKind.Local, "index the photos and videos of the source folder"),
		new("frames", App.Analysis, StepKind.Local, "take the frame at 1 s of every video"),
		new("tag", App.Analysis, StepKind.Local, "machine tags of every file from the vision model"),
		new("embed", App.Analysis, StepKind.Local, "SigLIP image vectors"),
		new("vocab", App.Tags, StepKind.Cloud, "vocabulary from the machine tags with a cloud model"),
		new("review", App.Tags, StepKind.Review, "a person checks vocabulary.yaml and approves it"),
		new("push-tags", App.Tags, StepKind.Site, "create the new tags on the site"),
		new("upload", App.Tags, StepKind.Site, "copy the files into the site's storage"),
		new("sync", App.Tags, StepKind.Site, "let the site import the files, read their ids"),
		new("assign", App.Tags, StepKind.Site, "add the tags to the site's files"),
		new("push-embeddings", App.Tags, StepKind.Site, "write the vectors into the site's database"),
	];

	public static Step? Find(string name) => All.FirstOrDefault(s => s.Name == name);
}
