namespace HaruyasumiRyokouki.DataTool.Common.Tests;

public sealed class WorkspaceTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("haru-ws-").FullName;

	public void Dispose() => Directory.Delete(_root, recursive: true);

	private Workspace Create(string toml, string? env = null)
	{
		File.WriteAllText(Path.Combine(_root, Workspace.ConfigName), toml);
		if (env is not null)
			File.WriteAllText(Path.Combine(_root, ".env"), env);
		return Workspace.Open(_root);
	}

	[Fact]
	public void Missing_settings_file_is_a_clear_error()
	{
		var ex = Assert.Throws<WorkspaceException>(() => Workspace.Open(_root));
		Assert.Contains(Workspace.ConfigName, ex.Message);
	}

	[Fact]
	public void Invalid_toml_is_a_workspace_error()
	{
		Assert.Throws<WorkspaceException>(() => Create("source = "));
	}

	[Fact]
	public void Reads_dotted_settings()
	{
		var ws = Create("""
			source = "D:/Photos"
			[backend]
			api = "http://backend.test"
			empty = ""
			""");

		Assert.Equal("D:/Photos", ws.GetString("source"));
		Assert.Equal("http://backend.test", ws.GetString("backend.api"));
		Assert.Null(ws.GetString("backend.empty"));
		Assert.Null(ws.GetString("backend.missing"));
		Assert.Null(ws.GetString("source.deeper"));
	}

	[Fact]
	public void Require_prefers_settings_then_environment_and_has_no_default()
	{
		const string variable = "HARU_TEST_REQUIRE_API";
		Environment.SetEnvironmentVariable(variable, "http://from-env.test");
		try
		{
			Assert.Equal("http://from-file.test", Create("[backend]\napi = \"http://from-file.test\"").Require("backend.api", variable));
			Assert.Equal("http://from-env.test", Create("").Require("backend.api", variable));

			Environment.SetEnvironmentVariable(variable, null);
			var ex = Assert.Throws<WorkspaceException>(() => Create("").Require("backend.api", variable));
			Assert.Contains(variable, ex.Message);
			Assert.Contains("backend.api", ex.Message);
		}
		finally
		{
			Environment.SetEnvironmentVariable(variable, null);
		}
	}

	[Fact]
	public void Env_file_fills_missing_variables_but_does_not_override()
	{
		const string fromFile = "HARU_TEST_ENV_FROM_FILE";
		const string preset = "HARU_TEST_ENV_PRESET";
		Environment.SetEnvironmentVariable(preset, "kept");
		try
		{
			Create("", $"# secrets\n{fromFile}='secret value'\n{preset}=ignored\nnot a pair\n");

			Assert.Equal("secret value", Workspace.RequireSecret(fromFile));
			Assert.Equal("kept", Environment.GetEnvironmentVariable(preset));
		}
		finally
		{
			Environment.SetEnvironmentVariable(fromFile, null);
			Environment.SetEnvironmentVariable(preset, null);
		}
	}

	[Fact]
	public void Missing_secret_names_the_variable()
	{
		var ex = Assert.Throws<WorkspaceException>(() => Workspace.RequireSecret("HARU_TEST_NOT_SET"));
		Assert.Contains("HARU_TEST_NOT_SET", ex.Message);
	}
}
