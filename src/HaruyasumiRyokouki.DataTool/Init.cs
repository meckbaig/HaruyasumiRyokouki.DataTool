using HaruyasumiRyokouki.DataTool.Common;

namespace HaruyasumiRyokouki.DataTool;

/// <summary>
/// haru-data init: a new workspace with haru-data.toml, where every key has a comment, and a
/// .env with the names of the secrets. Nothing is overwritten.
/// </summary>
internal static class Init
{
	public static Workspace Create(string folder, string source)
	{
		string root = Path.GetFullPath(folder);
		string config = Path.Combine(root, Workspace.ConfigName);
		if (File.Exists(config))
			throw new WorkspaceException($"{config} already exists; this folder is a workspace already.");

		string sourcePath = Path.GetFullPath(source);
		if (!Directory.Exists(sourcePath))
			throw new WorkspaceException($"Source folder {sourcePath} does not exist.");

		Directory.CreateDirectory(root);
		File.WriteAllText(config, Template.Replace("{source}", sourcePath));
		string env = Path.Combine(root, ".env");
		if (!File.Exists(env))
			File.WriteAllText(env, EnvTemplate);

		var workspace = Workspace.Open(root);
		WorkDb.Open(workspace).Dispose();
		return workspace;
	}

	public const string Template = """
		# Settings of a haru-data workspace. Secrets go into .env next to this file or into
		# environment variables, never here. Steps read this file every time they start.
		# Paths are in single quotes: a Windows path can be pasted as it is. In double quotes
		# a backslash starts an escape sequence, so there write E:/Photos or E:\\Photos.

		# Folder with the photos and videos of the trip; subfolders are included.
		source = '{source}'

		# Language models: any OpenAI-compatible server, local or in the cloud.
		# [usage] below picks one for each task. See "Choosing models" in the README.
		[providers.local]
		type = "openai"
		base_url = "http://localhost:1234/v1"   # LM Studio; Ollama: http://localhost:11434/v1
		model = "qwen3-vl-8b-instruct"          # the vision model loaded in that server

		[providers.deepseek]
		type = "openai"
		base_url = "https://api.deepseek.com"
		model = "deepseek-flash"
		api_key_env = "DEEPSEEK_API_KEY"        # name of the variable in .env that holds the key
		json_mode = "object"                    # DeepSeek takes JSON mode but not a JSON schema
		max_tokens = 200000                     # reasoning is long; a low limit cuts the answer off
		# reasoning_effort = "none"             # turns thinking off; leave it on for the vocabulary

		[usage]
		vision = "local"           # tag: a vision model, once per photo
		vocabulary = "deepseek"    # vocab: one smart model over the whole list of tags

		[vision]
		max_side = 1024            # photos are scaled down to this before they go to the model
		concurrency = 1            # parallel requests; 1 for a local server, 16-32 for a cloud API

		[embed]
		model = "google/siglip2-base-patch16-256"   # must match the site's vectors; do not change
		device = "auto"                             # auto | cuda | mps | cpu

		[vocabulary]
		translate_concurrency = 4  # parallel translations by the backend's AI; low for a free tier

		[backend]
		api = ""                   # address of the site's API, or HARU_API; no default on purpose

		# The folder the site imports new media from (backend MediaStorage); upload copies the
		# photos there. The same folder as the backend's LocalStorageProvider:Path, as seen from
		# this computer: a local path, or a network share of the server.
		[storage]
		type = "local"
		path = ''
		concurrency = 4            # parallel copies
		# If the backend keeps media in Nextcloud instead (MediaStorage:Provider = WebDav):
		# type = "webdav"
		# url = ""                 # the media folder, or HARU_STORAGE_URL; login and password in .env
		""";

	public const string EnvTemplate = """
		# Secrets of this workspace. Variables set in the environment win over this file.
		DEEPSEEK_API_KEY=
		# The site's admin (Basic auth)
		HARU_USER=
		HARU_PASSWORD=
		# Only for [storage] type = "webdav" (Nextcloud)
		# HARU_STORAGE_USER=
		# HARU_STORAGE_PASSWORD=
		# The site's Postgres: reading media_files, writing media_embeddings,
		# like Host=db.example;Port=5432;Database=haru;Username=haru;Password=...
		HARU_CONNECTION=
		""";
}
