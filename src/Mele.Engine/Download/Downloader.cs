using System.Security.Cryptography;
using System.Text.Json;
using Mele.Engine.Biq;
using Mele.Engine.Nexus;

namespace Mele.Engine.Download;

public enum FetchResult { Cached, Downloaded }

/// <summary>What the install pipeline needs from a downloader; lets tests supply archives without a network.</summary>
public interface IModFileSource
{
    Task<(string Path, FetchResult Result)> FetchAsync(ModEntry entry, CancellationToken ct = default);
    void Evict(int fileId);
}

/// <summary>
/// Downloads a Nexus file into <c>cacheDir/&lt;fileId&gt;/&lt;file_name&gt;</c> (+ info.json recording the expected size),
/// verifying size and, when Nexus supplies it, MD5. A file only counts as cached if info.json exists and the size matches.
/// </summary>
public sealed class Downloader(NexusClient nexus, HttpClient cdn, string cacheDir) : IModFileSource
{
    public string CacheDir { get; } = cacheDir;

    private sealed record Info(string FileName, long SizeInBytes);

    public string? FindCached(int fileId)
    {
        var dir = Path.Combine(CacheDir, fileId.ToString());
        var infoPath = Path.Combine(dir, "info.json");
        if (!File.Exists(infoPath)) return null;
        var info = JsonSerializer.Deserialize<Info>(File.ReadAllText(infoPath));
        if (info == null) return null;
        var file = Path.Combine(dir, Path.GetFileName(info.FileName));
        return File.Exists(file) && new FileInfo(file).Length == info.SizeInBytes ? file : null;
    }

    public async Task<(string Path, FetchResult Result)> FetchAsync(ModEntry entry, CancellationToken ct = default)
        => await FetchAsync(entry.GameDomain ?? throw new NexusException($"{entry.Name}: no Nexus link in group file"),
            entry.ModId!.Value, entry.FileId!.Value, ct);

    public async Task<(string Path, FetchResult Result)> FetchAsync(string game, int modId, int fileId, CancellationToken ct = default)
    {
        if (FindCached(fileId) is { } hit) return (hit, FetchResult.Cached);

        var info = await nexus.GetFileInfoAsync(game, modId, fileId, ct);
        var dir = Path.Combine(CacheDir, fileId.ToString());
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(info.FileName));
        var part = dest + ".part";

        var url = await nexus.GetDownloadUrlAsync(game, modId, fileId, ct);
        using (var resp = await cdn.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(part);
            await src.CopyToAsync(dst, 1 << 20, ct);
        }

        var size = new FileInfo(part).Length;
        if (info.SizeInBytes > 0 && size != info.SizeInBytes)
        {
            File.Delete(part);
            throw new NexusException($"File {fileId}: size mismatch (got {size}, want {info.SizeInBytes})");
        }
        if (info.Md5 != null && await Md5Async(part, ct) != info.Md5)
        {
            File.Delete(part);
            throw new NexusException($"File {fileId}: md5 mismatch");
        }
        File.Move(part, dest, overwrite: true);
        File.WriteAllText(Path.Combine(dir, "info.json"), JsonSerializer.Serialize(new Info(Path.GetFileName(dest), size)));
        return (dest, FetchResult.Downloaded);
    }

    public static async Task<string> Md5Async(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await MD5.Create().ComputeHashAsync(fs, ct)).ToLowerInvariant();
    }

    /// <summary>Deletes a file's cache folder once it has been staged (archives are large).</summary>
    public void Evict(int fileId)
    {
        var dir = Path.Combine(CacheDir, fileId.ToString());
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
