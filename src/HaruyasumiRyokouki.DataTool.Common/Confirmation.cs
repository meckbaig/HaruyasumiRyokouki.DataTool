namespace HaruyasumiRyokouki.DataTool.Common;

/// <summary>
/// The question every step that writes outside the workspace asks first: it prints where it
/// is going to write (API host, database, storage) and waits for an explicit yes.
/// </summary>
public static class Confirmation
{
	/// <summary>Prints the target and asks; returns true only on <c>y</c> or <c>yes</c>, or when <paramref name="yes"/> is set.</summary>
	/// <param name="action">What will happen, such as "Create 12 tags".</param>
	/// <param name="targets">Where it goes, one line each, such as "API: https://haru.example".</param>
	/// <param name="yes">The <c>--yes</c> flag: the targets are still printed, the question is skipped.</param>
	/// <param name="input">Where the answer is read from; a redirected input without <paramref name="yes"/> means no.</param>
	public static bool Ask(string action, IEnumerable<string> targets, bool yes, TextReader input, TextWriter output, bool inputRedirected)
	{
		output.WriteLine(action);
		foreach (string target in targets)
			output.WriteLine("  " + target);

		if (yes)
		{
			output.WriteLine("--yes given, continuing.");
			return true;
		}

		if (inputRedirected)
		{
			output.WriteLine("No terminal to ask in. Run again with --yes if this is the right target.");
			return false;
		}

		output.Write("Continue? [y/N] ");
		string answer = (input.ReadLine() ?? "").Trim();
		return answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary><see cref="Ask(string, IEnumerable{string}, bool, TextReader, TextWriter, bool)"/> on the console.</summary>
	public static bool Ask(string action, IEnumerable<string> targets, bool yes)
		=> Ask(action, targets, yes, Console.In, Console.Out, Console.IsInputRedirected);

	/// <summary>The host of a URL for printing, without credentials, path or query.</summary>
	public static string Host(string url)
		=> Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Scheme + "://" + uri.Authority : url;
}
