using System.Diagnostics;
using HaruyasumiRyokouki.DataTool.Common;

namespace HaruyasumiRyokouki.DataTool;

/// <summary>
/// haru-data doctor: what the steps need and whether it is there, so a missing program or an
/// unreachable server shows up before the step that needs it. Prints no secrets, only whether
/// they are set.
/// </summary>
internal sealed class Doctor(RepoLayout repo, TextWriter output)
{
	/// <summary>Seconds to wait for a server.</summary>
	private static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(5);

	private int _missing;

	/// <summary>Returns 0 when everything needed is there, 1 otherwise.</summary>
	public async Task<int> RunAsync(Workspace? workspace)
	{
		output.WriteLine("Programs");
		string? uv = Version("uv", "--version");
		Check(uv is not null, "uv", uv ?? "not found: install uv (see README), needed by the analysis steps");
		string? ffmpeg = Version("ffmpeg", "-version");
		Check(ffmpeg is not null, "ffmpeg", ffmpeg ?? "not found: install ffmpeg, needed for the frames of videos");
		Info(".NET", Environment.Version.ToString());
		Info("analysis packages", Directory.Exists(Path.Combine(repo.AnalysisProject, ".venv"))
			? "installed"
			: "not installed yet: the first analysis step installs them into .cache (several GB with torch)");
		Info("SigLIP model", Directory.Exists(Path.Combine(repo.CacheDirectory, "huggingface", "hub", "models--google--siglip2-base-patch16-256"))
			? "downloaded"
			: "not downloaded yet: the embed step downloads it into .cache (about 1.5 GB)");

		if (workspace is not null)
		{
			output.WriteLine();
			output.WriteLine("Workspace " + workspace.Root);
			string? source = workspace.GetString("source");
			Check(source is not null && Directory.Exists(Path.GetFullPath(source, workspace.Root)), "source folder", source ?? "not set");

			await CheckProviderAsync(workspace, "vision");
			await CheckProviderAsync(workspace, "vocabulary");

			string? api = workspace.GetString("backend.api") ?? NonEmpty(Environment.GetEnvironmentVariable("HARU_API"));
			if (api is null)
				Check(false, "backend", "[backend] api is not set");
			else
				Check(await ReachableAsync(api.TrimEnd('/') + "/health"), "backend", Confirmation.Host(api));
			Secret("HARU_USER");
			Secret("HARU_PASSWORD");

			string? storage = workspace.GetString("storage.type");
			if (storage == "local")
			{
				string? path = workspace.GetString("storage.path");
				Check(path is not null && Directory.Exists(Path.GetFullPath(path, workspace.Root)), "storage", "folder " + (path ?? "not set"));
			}
			else if (storage == "webdav")
			{
				string? url = workspace.GetString("storage.url") ?? NonEmpty(Environment.GetEnvironmentVariable("HARU_STORAGE_URL"));
				Check(url is not null, "storage", url is null ? "[storage] url is not set" : "WebDAV " + Confirmation.Host(url));
				Secret("HARU_STORAGE_USER");
				Secret("HARU_STORAGE_PASSWORD");
			}
			else
				Check(false, "storage", "[storage] type is not set: webdav or local");
			Secret("HARU_CONNECTION");
		}

		output.WriteLine();
		output.WriteLine(_missing == 0 ? "Everything is in place."
			: $"Missing: {_missing}. The steps that need these will stop with the same message.");
		return _missing == 0 ? ExitCodes.Ok : ExitCodes.Error;
	}

	private async Task CheckProviderAsync(Workspace workspace, string task)
	{
		string? name = workspace.GetString($"usage.{task}");
		string? baseUrl = name is null ? null : workspace.GetString($"providers.{name}.base_url");
		if (name is null || baseUrl is null)
		{
			Check(false, $"{task} model", name is null ? $"[usage] {task} is not set" : $"[providers.{name}] base_url is not set");
			return;
		}

		string? keyVariable = workspace.GetString($"providers.{name}.api_key_env");
		if (keyVariable is not null && NonEmpty(Environment.GetEnvironmentVariable(keyVariable)) is null)
		{
			Check(false, $"{task} model", $"{name}: {keyVariable} is not set");
			return;
		}

		// A cloud API answers /models only with the key; that it answers at all is enough here.
		bool local = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;
		string where = $"{name}: {workspace.GetString($"providers.{name}.model")} at {Confirmation.Host(baseUrl)}";
		if (local)
			Check(await ReachableAsync(baseUrl.TrimEnd('/') + "/models"), $"{task} model", where + (task == "vision" ? " (start the server and load the model before tag)" : ""));
		else
			Info($"{task} model", where);
	}

	private async Task<bool> ReachableAsync(string url)
	{
		using var http = new HttpClient { Timeout = ServerTimeout };
		try
		{
			using var response = await http.GetAsync(url);
			return (int)response.StatusCode < 500;
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
		{
			return false;
		}
	}

	private void Secret(string name) => Check(NonEmpty(Environment.GetEnvironmentVariable(name)) is not null, name, "set", "not set (environment or .env)");

	private void Check(bool ok, string what, string detail, string? missingDetail = null)
	{
		if (!ok)
			_missing++;
		output.WriteLine($"  {(ok ? "ok     " : "MISSING")} {what,-20} {(ok ? detail : missingDetail ?? detail)}");
	}

	private void Info(string what, string detail) => output.WriteLine($"  {"",-7} {what,-20} {detail}");

	/// <summary>First line of the program's version output, or null when it cannot be started.</summary>
	private static string? Version(string program, string argument)
	{
		try
		{
			using var process = Process.Start(new ProcessStartInfo(program, argument) { RedirectStandardOutput = true, UseShellExecute = false });
			string? line = process?.StandardOutput.ReadLine();
			process?.StandardOutput.ReadToEnd();
			process?.WaitForExit();
			return line;
		}
		catch (System.ComponentModel.Win32Exception)
		{
			return null;
		}
	}

	private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
