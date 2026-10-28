using HaruyasumiRyokouki.DataTool.Common;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Tests;

public sealed class InitTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("haru-init-").FullName;

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		Directory.Delete(_root, recursive: true);
	}

	[Fact]
	public void Creates_settings_the_apps_can_read_and_never_overwrites_them()
	{
		string source = Directory.CreateDirectory(Path.Combine(_root, "Photos Japan")).FullName;
		string folder = Path.Combine(_root, "my-trip");

		var workspace = Init.Create(folder, source);

		// The path as the system writes it, backslashes on Windows: the template quotes it so.
		Assert.Equal(source, workspace.GetString("source"));
		Assert.Equal("local", workspace.GetString("usage.vision"));
		Assert.Equal("object", workspace.GetString("providers.deepseek.json_mode"));
		Assert.Equal(200000, workspace.GetInt("providers.deepseek.max_tokens"));
		// No real address by default: the steps that write stop until one is set.
		Assert.Null(workspace.GetString("backend.api"));
		Assert.Equal("local", workspace.GetString("storage.type"));
		Assert.Null(workspace.GetString("storage.path"));
		Assert.True(File.Exists(workspace.DbPath));
		Assert.Contains("HARU_CONNECTION=", File.ReadAllText(Path.Combine(folder, ".env")));

		Assert.Throws<WorkspaceException>(() => Init.Create(folder, source));
		Assert.Throws<WorkspaceException>(() => Init.Create(Path.Combine(_root, "other"), Path.Combine(_root, "missing")));
	}
}
