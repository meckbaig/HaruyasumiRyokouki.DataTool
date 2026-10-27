namespace TagsTool;

/// <summary>Разбор аргументов вида --key value и --flag.</summary>
internal sealed class Options
{
	private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

	public bool Has(string key) => _values.ContainsKey(key);

	public string? Get(string key) => _values.GetValueOrDefault(key);

	public string Require(string key) => Get(key)
		?? throw new ArgumentException($"Не хватает обязательного параметра --{key}");

	/// <summary>Аргумент, либо переменная окружения, либо значение по умолчанию.</summary>
	public string? GetOrEnv(string key, string environmentVariable, string? fallback = null)
		=> Get(key)
		?? Environment.GetEnvironmentVariable(environmentVariable)
		?? fallback;

	public static Options Parse(string[] args)
	{
		var options = new Options();

		for (int i = 0; i < args.Length; i++)
		{
			if (!args[i].StartsWith("--"))
				continue;

			string key = args[i][2..];
			string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--")
				? args[++i]
				: null;

			options._values[key] = value;
		}

		return options;
	}
}
