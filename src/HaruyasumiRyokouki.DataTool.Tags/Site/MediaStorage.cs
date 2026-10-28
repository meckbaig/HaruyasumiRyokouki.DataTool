using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using HaruyasumiRyokouki.DataTool.Common;

namespace HaruyasumiRyokouki.DataTool.Tags.Site;

/// <summary>
/// The folder the site imports media from, as the backend's IFileStorage sees it: one flat folder,
/// no subfolders. The backend takes the date of a file from its name, otherwise from its
/// modification time in the storage, so an upload keeps the original modification time.
/// </summary>
internal interface IMediaStorage : IDisposable
{
	/// <summary>Where the files go, for printing before writing; no credentials.</summary>
	string Description { get; }

	/// <summary>Names of the files in the storage.</summary>
	Task<List<string>> ListAsync(CancellationToken cancellationToken);

	/// <summary>Creates or overwrites the file and sets its modification time.</summary>
	Task UploadAsync(string localPath, string fileName, DateTime modifiedUtc, CancellationToken cancellationToken);
}

internal static class MediaStorage
{
	/// <summary>
	/// The storage of <c>[storage]</c>: <c>type = "local"</c> with <c>path</c>, or <c>type = "webdav"</c>
	/// with <c>url</c> (or HARU_STORAGE_URL) and HARU_STORAGE_USER, HARU_STORAGE_PASSWORD.
	/// </summary>
	public static IMediaStorage FromWorkspace(Workspace workspace)
	{
		string type = workspace.GetString("storage.type")
			?? throw new WorkspaceException("Set [storage] type in haru-data.toml: \"webdav\" or \"local\".");

		switch (type)
		{
			case "local":
				string path = workspace.GetString("storage.path")
					?? throw new WorkspaceException("Set [storage] path in haru-data.toml: the folder the backend imports from.");
				return new LocalMediaStorage(Path.GetFullPath(path, workspace.Root));
			case "webdav":
				string url = workspace.Require("storage.url", "HARU_STORAGE_URL");
				if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
					throw new WorkspaceException($"storage url '{Confirmation.Host(url)}' is not an absolute URL.");
				return new WebDavMediaStorage(uri, Workspace.RequireSecret("HARU_STORAGE_USER"), Workspace.RequireSecret("HARU_STORAGE_PASSWORD"));
			default:
				throw new WorkspaceException($"[storage] type '{type}' is not supported: use \"webdav\" or \"local\".");
		}
	}
}

/// <summary>A local folder, as the backend's LocalStorageProvider.</summary>
internal sealed class LocalMediaStorage(string folder) : IMediaStorage
{
	/// <summary>Suffix of a file being copied; the backend skips it as an unknown type.</summary>
	public const string PartSuffix = ".uploading";

	public string Description => "storage: folder " + folder;

	public Task<List<string>> ListAsync(CancellationToken cancellationToken)
	{
		if (!Directory.Exists(folder))
			throw new WorkspaceException($"Storage folder {folder} does not exist.");

		return Task.FromResult(Directory.EnumerateFiles(folder).Select(f => Path.GetFileName(f)).Where(n => !n.EndsWith(PartSuffix)).ToList());
	}

	public async Task UploadAsync(string localPath, string fileName, DateTime modifiedUtc, CancellationToken cancellationToken)
	{
		// Copied under another name first: an interrupted copy must not look like an uploaded file.
		string target = Path.Combine(folder, fileName);
		string part = target + PartSuffix;
		await using (var source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
		await using (var destination = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
			await source.CopyToAsync(destination, cancellationToken);

		File.SetLastWriteTimeUtc(part, modifiedUtc);
		File.Move(part, target, overwrite: true);
	}

	public void Dispose() { }
}

/// <summary>
/// A WebDAV folder, as the backend's WebDavStorageProvider. Nextcloud and ownCloud take the
/// modification time from the X-OC-MTime header; other servers ignore it and use the upload time.
/// </summary>
internal sealed class WebDavMediaStorage : IMediaStorage
{
	private static readonly XNamespace Dav = "DAV:";
	private readonly HttpClient _http;

	public WebDavMediaStorage(Uri folder, string user, string password, HttpMessageHandler? handler = null)
	{
		_http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
		{
			BaseAddress = new Uri(folder.ToString().TrimEnd('/') + "/"),
			// Videos are large; a slow upload is not a hung one.
			Timeout = TimeSpan.FromHours(1)
		};
		string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
		_http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
	}

	public string Description => "storage: WebDAV " + Confirmation.Host(_http.BaseAddress!.ToString()) + _http.BaseAddress!.AbsolutePath;

	public async Task<List<string>> ListAsync(CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), "")
		{
			Content = new StringContent("""<?xml version="1.0"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/></d:prop></d:propfind>""",
				Encoding.UTF8, "application/xml")
		};
		request.Headers.Add("Depth", "1");

		using var response = await _http.SendAsync(request, cancellationToken);
		if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
			throw new InvalidOperationException($"The storage answers {(int)response.StatusCode}: HARU_STORAGE_USER or HARU_STORAGE_PASSWORD is wrong.");
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"listing the storage: HTTP {(int)response.StatusCode}");

		var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
		return xml.Descendants(Dav + "response")
			// Folders, including the listed folder itself, have a collection resource type.
			.Where(r => !r.Descendants(Dav + "collection").Any())
			.Select(r => (string?)r.Element(Dav + "href"))
			.Where(href => href is not null)
			.Select(href => Uri.UnescapeDataString(href!.TrimEnd('/').Split('/')[^1]))
			.ToList();
	}

	public async Task UploadAsync(string localPath, string fileName, DateTime modifiedUtc, CancellationToken cancellationToken)
	{
		await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
		using var request = new HttpRequestMessage(HttpMethod.Put, Uri.EscapeDataString(fileName)) { Content = new StreamContent(file) };
		request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
		request.Headers.Add("X-OC-MTime", new DateTimeOffset(modifiedUtc).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

		using var response = await _http.SendAsync(request, cancellationToken);
		if (!response.IsSuccessStatusCode)
			throw new HttpRequestException($"uploading {fileName}: HTTP {(int)response.StatusCode}", null, response.StatusCode);
	}

	public void Dispose() => _http.Dispose();
}
