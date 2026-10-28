using Tomlyn;
using Tomlyn.Model;

namespace HaruyasumiRyokouki.DataTool.Common;

/// <summary>
/// A workspace folder: settings from <c>haru-data.toml</c>, secrets from <c>.env</c> and the paths
/// every step shares. The layout is described in <c>docs/formats.md</c>.
/// </summary>
public sealed class Workspace
{
	/// <summary>Name of the settings file in the workspace folder.</summary>
	public const string ConfigName = "haru-data.toml";

	private Workspace(string root, TomlTable settings)
	{
		Root = root;
		Settings = settings;
	}

	/// <summary>Absolute path of the workspace folder.</summary>
	public string Root { get; }

	/// <summary>Parsed <c>haru-data.toml</c>; read values with <see cref="GetString"/>.</summary>
	public TomlTable Settings { get; }

	/// <summary>Path of the shared state database.</summary>
	public string DbPath => Path.Combine(Root, "work.sqlite");

	/// <summary>Folder for <c>reports/&lt;step&gt;.json</c>.</summary>
	public string ReportsDir => Path.Combine(Root, "reports");

	/// <summary>Folder for cached cloud LLM answers.</summary>
	public string LlmCacheDir => Path.Combine(Root, "cache", "llm");

	/// <summary>The vocabulary file the user reviews.</summary>
	public string VocabularyPath => Path.Combine(Root, "vocabulary.yaml");

	/// <summary>
	/// Opens the workspace and loads its <c>.env</c> into the process environment
	/// (variables that are already set win).
	/// </summary>
	/// <exception cref="WorkspaceException">No settings file, or it is not valid TOML.</exception>
	public static Workspace Open(string path)
	{
		string root = Path.GetFullPath(path);
		string configPath = Path.Combine(root, ConfigName);

		if (!File.Exists(configPath))
			throw new WorkspaceException(
				$"{configPath} not found. Create the workspace with 'haru-data init' or write the settings file by hand (see README).");

		TomlTable settings;
		try
		{
			settings = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(configPath)) ?? new TomlTable();
		}
		catch (TomlException ex)
		{
			throw new WorkspaceException($"{configPath}: {ex.Message}");
		}

		var workspace = new Workspace(root, settings);
		EnvFile.Load(Path.Combine(root, ".env"));
		return workspace;
	}

	/// <summary>
	/// A string setting by dotted path, such as <c>backend.api</c>, or null when it is missing or empty.
	/// </summary>
	public string? GetString(string dottedPath)
	{
		object? node = Settings;
		foreach (string part in dottedPath.Split('.'))
		{
			if (node is not TomlTable table || !table.TryGetValue(part, out node))
				return null;
		}

		return node is string text && text.Length > 0 ? text : null;
	}

	/// <summary>
	/// A setting that may also come from an environment variable; the settings file wins.
	/// There are no defaults: a missing value is an error that names both places.
	/// </summary>
	/// <exception cref="WorkspaceException">Neither the setting nor the variable is set.</exception>
	public string Require(string dottedPath, string environmentVariable)
	{
		return GetString(dottedPath)
			?? NonEmpty(Environment.GetEnvironmentVariable(environmentVariable))
			?? throw new WorkspaceException(
				$"Set {dottedPath} in {ConfigName} or the {environmentVariable} environment variable.");
	}

	/// <summary>A secret that may only come from the environment or <c>.env</c>, never from the settings file.</summary>
	/// <exception cref="WorkspaceException">The variable is not set.</exception>
	public static string RequireSecret(string environmentVariable)
	{
		return NonEmpty(Environment.GetEnvironmentVariable(environmentVariable))
			?? throw new WorkspaceException(
				$"Set the {environmentVariable} environment variable or put it into .env in the workspace.");
	}

	private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>A problem with the workspace the user has to fix before a step can run.</summary>
public sealed class WorkspaceException(string message) : Exception(message);
