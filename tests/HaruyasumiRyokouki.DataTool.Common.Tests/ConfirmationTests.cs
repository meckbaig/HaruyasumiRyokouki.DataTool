namespace HaruyasumiRyokouki.DataTool.Common.Tests;

public sealed class ConfirmationTests
{
	private static (bool Answer, string Output) Ask(string typed, bool yes = false, bool redirected = false)
	{
		var output = new StringWriter();
		bool answer = Confirmation.Ask("Create 3 tags", ["API: https://haru.test"], yes, new StringReader(typed), output, redirected);
		return (answer, output.ToString());
	}

	[Theory]
	[InlineData("y\n", true)]
	[InlineData("YES\n", true)]
	[InlineData("\n", false)]
	[InlineData("n\n", false)]
	[InlineData("", false)]
	public void Only_an_explicit_yes_continues(string typed, bool expected)
	{
		var (answer, output) = Ask(typed);
		Assert.Equal(expected, answer);
		Assert.Contains("API: https://haru.test", output);
		Assert.Contains("[y/N]", output);
	}

	[Fact]
	public void Yes_flag_skips_the_question_but_still_prints_the_target()
	{
		var (answer, output) = Ask("", yes: true);
		Assert.True(answer);
		Assert.Contains("API: https://haru.test", output);
		Assert.DoesNotContain("[y/N]", output);
	}

	[Fact]
	public void Without_a_terminal_and_without_yes_the_answer_is_no()
	{
		var (answer, output) = Ask("y\n", redirected: true);
		Assert.False(answer);
		Assert.Contains("--yes", output);
	}

	[Fact]
	public void Host_drops_credentials_and_path()
	{
		Assert.Equal("https://haru.test:8443", Confirmation.Host("https://user:pass@haru.test:8443/dav/folder?x=1"));
	}
}
