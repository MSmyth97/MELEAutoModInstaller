using System.Net.Http.Headers;
using System.Text.Json;

namespace Mele.Engine.Nexus;

public sealed class NexusException(string message) : Exception(message);

public sealed record NexusUser(string Name, bool IsPremium);
public sealed record NexusFileInfo(int FileId, string FileName, string Name, long SizeInBytes, string? Md5, string Category, bool SizeIsRounded = false)
{
    /// <summary>True when the file's real size is within a KB of <see cref="SizeInBytes"/> (Nexus gave only <c>size_kb</c>).</summary>
    public bool SizeMatches(long actual) => SizeInBytes <= 0 || (SizeIsRounded ? Math.Abs(actual - SizeInBytes) < 1024 : actual == SizeInBytes);
}

/// <summary>Minimal Nexus Mods API v1 client. Premium accounts get download links without an nxm key.</summary>
public sealed class NexusClient
{
    public const string ApiBase = "https://api.nexusmods.com/v1/";
    private readonly HttpClient _http;

    public NexusClient(HttpClient http, string apiKey, string appVersion = "0.1.0")
    {
        _http = http;
        _http.BaseAddress ??= new Uri(ApiBase);
        _http.DefaultRequestHeaders.Remove("apikey");
        _http.DefaultRequestHeaders.Add("apikey", apiKey);
        _http.DefaultRequestHeaders.Remove("Application-Name");
        _http.DefaultRequestHeaders.Add("Application-Name", "MELEAutoModInstaller");
        _http.DefaultRequestHeaders.Remove("Application-Version");
        _http.DefaultRequestHeaders.Add("Application-Version", appVersion);
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MELEAutoModInstaller", appVersion));
    }

    public async Task<NexusUser> ValidateAsync(CancellationToken ct = default)
    {
        using var doc = await GetAsync("users/validate.json", ct);
        var r = doc.RootElement;
        return new NexusUser(r.GetProperty("name").GetString() ?? "", r.TryGetProperty("is_premium", out var p) && p.GetBoolean());
    }

    public async Task<NexusFileInfo> GetFileInfoAsync(string game, int modId, int fileId, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"games/{game}/mods/{modId}/files/{fileId}.json", ct);
        return ParseFile(doc.RootElement);
    }

    public async Task<IReadOnlyList<NexusFileInfo>> ListFilesAsync(string game, int modId, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"games/{game}/mods/{modId}/files.json", ct);
        return doc.RootElement.GetProperty("files").EnumerateArray().Select(ParseFile).ToList();
    }

    /// <summary>
    /// Returns the CDN URL. CDN paths can contain spaces; <see cref="Uri"/> escapes them when the string is
    /// used to build the request, so callers must pass the result through <c>new Uri(...)</c> (not string concat).
    /// </summary>
    public async Task<Uri> GetDownloadUrlAsync(string game, int modId, int fileId, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"games/{game}/mods/{modId}/files/{fileId}/download_link.json", ct);
        var links = doc.RootElement;
        if (links.ValueKind != JsonValueKind.Array || links.GetArrayLength() == 0)
            throw new NexusException($"No download link for {modId}/{fileId}");
        return new Uri(links[0].GetProperty("URI").GetString()!);
    }

    private static NexusFileInfo ParseFile(JsonElement f) => new(
        f.GetProperty("file_id").GetInt32(),
        f.GetProperty("file_name").GetString() ?? "",
        f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
        f.TryGetProperty("size_in_bytes", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64()
            : f.TryGetProperty("size_kb", out var kb) && kb.ValueKind == JsonValueKind.Number ? kb.GetInt64() * 1024 : 0,
        f.TryGetProperty("md5", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()!.ToLowerInvariant() : null,
        f.TryGetProperty("category_name", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "",
        !(f.TryGetProperty("size_in_bytes", out var exact) && exact.ValueKind == JsonValueKind.Number));

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(path, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new NexusException($"HTTP {(int)resp.StatusCode} for {path}: {body[..Math.Min(body.Length, 200)]}");
        }
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }
}
