namespace HaruyasumiRyokouki.DataTool.Common;

/// <summary>Reads <c>KEY=VALUE</c> lines of a <c>.env</c> file into the process environment.</summary>
public static class EnvFile
{
	/// <summary>
	/// Loads the file if it exists. Variables already set in the environment are kept, so a
	/// value given on the command line or by the orchestrator wins over the file.
	/// </summary>
	public static void Load(string path)
	{
		if (!File.Exists(path))
			return;

		foreach (string rawLine in File.ReadAllLines(path))
		{
			string line = rawLine.Trim();
			int equals = line.IndexOf('=');
			if (line.Length == 0 || line.StartsWith('#') || equals <= 0)
				continue;

			string key = line[..equals].Trim();
			string value = line[(equals + 1)..].Trim().Trim('"', '\'');

			if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
				Environment.SetEnvironmentVariable(key, value);
		}
	}
}
