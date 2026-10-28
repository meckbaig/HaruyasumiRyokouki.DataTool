using System.Diagnostics;
using HaruyasumiRyokouki.DataTool.Common;

namespace HaruyasumiRyokouki.DataTool;

/// <summary>Starts a step of an app and returns its exit code (docs/formats.md).</summary>
internal interface IStepLauncher
{
	int Run(App app, string step, IReadOnlyList<string> arguments);
}

/// <summary>
/// Starts the apps as processes with the console of haru-data, so their progress and questions
/// reach the person as they are: haru-analysis through uv, haru-tags from the build output next
/// to haru-data. Everything uv and Hugging Face download goes into .cache of the repository.
/// </summary>
internal sealed class ProcessLauncher(RepoLayout repo, string workspaceRoot) : IStepLauncher
{
	public int Run(App app, string step, IReadOnlyList<string> arguments)
	{
		var start = app == App.Analysis
			? new ProcessStartInfo("uv") { ArgumentList = { "run", "--project", repo.AnalysisProject, "haru-analysis" } }
			: repo.TagsStartInfo();
		start.UseShellExecute = false;
		start.ArgumentList.Add(step);
		foreach (string argument in arguments)
			start.ArgumentList.Add(argument);
		start.ArgumentList.Add("--workspace");
		start.ArgumentList.Add(workspaceRoot);
		foreach (var (name, value) in repo.CacheEnvironment())
			start.Environment[name] = value;

		// Ctrl+C reaches the child, which saves its work and exits; haru-data waits for it.
		ConsoleCancelEventHandler keep = (_, e) => e.Cancel = true;
		Console.CancelKeyPress += keep;
		try
		{
			using var process = Process.Start(start)
				?? throw new WorkspaceException($"Cannot start {start.FileName}.");
			process.WaitForExit();
			return process.ExitCode;
		}
		catch (System.ComponentModel.Win32Exception) when (app == App.Analysis)
		{
			throw new WorkspaceException("uv is not installed or not on PATH; see the README. Run haru-data doctor.");
		}
		finally
		{
			Console.CancelKeyPress -= keep;
		}
	}
}

/// <summary>
/// Where the parts of the tool are: haru-data runs from a clone of the repository, built with
/// dotnet build, so the analysis project is found next to the build output.
/// </summary>
internal sealed class RepoLayout(string root, string binDirectory)
{
	public string Root { get; } = root;

	public string AnalysisProject => Path.Combine(Root, "src", "analysis");

	public string CacheDirectory => Path.Combine(Root, ".cache");

	/// <summary>The repository that contains the running haru-data.</summary>
	/// <exception cref="WorkspaceException">haru-data does not run from a clone.</exception>
	public static RepoLayout Find()
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
			if (File.Exists(Path.Combine(dir.FullName, "src", "analysis", "pyproject.toml")))
				return new RepoLayout(dir.FullName, AppContext.BaseDirectory);

		throw new WorkspaceException("haru-data must run from a clone of the repository, built with dotnet build (see README).");
	}

	/// <summary>haru-tags, built next to haru-data by the project reference.</summary>
	public ProcessStartInfo TagsStartInfo()
	{
		string exe = Path.Combine(binDirectory, OperatingSystem.IsWindows() ? "haru-tags.exe" : "haru-tags");
		if (File.Exists(exe))
			return new ProcessStartInfo(exe);

		string dll = Path.Combine(binDirectory, "haru-tags.dll");
		if (File.Exists(dll))
			return new ProcessStartInfo("dotnet") { ArgumentList = { dll } };

		throw new WorkspaceException($"haru-tags is not built next to haru-data in {binDirectory}. Run dotnet build.");
	}

	/// <summary>
	/// Variables that keep packages, Python and models in .cache (as uv-local.cmd does);
	/// values already set by the person win.
	/// </summary>
	public IEnumerable<(string Name, string Value)> CacheEnvironment()
	{
		var values = new (string, string)[]
		{
			("UV_CACHE_DIR", Path.Combine(CacheDirectory, "uv")),
			("UV_PYTHON_INSTALL_DIR", Path.Combine(CacheDirectory, "python")),
			("HF_HOME", Path.Combine(CacheDirectory, "huggingface")),
		};
		return values.Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v.Item1)));
	}
}
