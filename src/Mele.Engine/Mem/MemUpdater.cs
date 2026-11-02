using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mele.Engine.Archive;

namespace Mele.Engine.Mem;

public sealed class MemUpdateException(string message) : Exception(message);

/// <summary>
/// Installs/updates MassEffectModderNoGui from the official GitHub releases. Windows gets the .7z build,
/// Linux/macOS the native zip. Takes the newest stable release unless a version is pinned.
/// </summary>
public sealed class MemUpdater(HttpClient http, IArchiveExtractor extractor, IProcessRunner runner, string installDir)
{
    private const string ReleasesUrl = "https://api.github.com/repos/MassEffectModder/MassEffectModder/releases?per_page=15";

    public static string ExeName => OperatingSystem.IsWindows() ? "MassEffectModderNoGui.exe" : "MassEffectModderNoGui";
    public string ExePath => Path.Combine(installDir, ExeName);

    /// <summary>Asset name prefix for this OS; Windows' ("…NoGui-v") must not match the "-Linux-"/"-macOS-" ones.</summary>
    public static Func<string, bool> AssetMatcher(OSPlatform? os = null)
    {
        os ??= OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;
        if (os == OSPlatform.Linux) return n => n.StartsWith("MassEffectModderNoGui-Linux-v") && n.EndsWith(".zip");
        if (os == OSPlatform.OSX) return n => n.StartsWith("MassEffectModderNoGui-macOS-v") && n.EndsWith(".zip");
        return n => n.StartsWith("MassEffectModderNoGui-v") && (n.EndsWith(".7z") || n.EndsWith(".zip"));
    }

    /// <returns>Path to the MEM executable, updated if needed.</returns>
    public async Task<string> EnsureAsync(IProgress<string>? status = null, int? pinnedVersion = null, CancellationToken ct = default)
    {
        var installed = File.Exists(ExePath) ? await new MemRunner(runner, ExePath).VersionAsync(ct) ?? 0 : 0;

        status?.Report("Checking MassEffectModderNoGui releases");
        using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
        req.Headers.UserAgent.ParseAdd("MELEAutoModInstaller");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var match = AssetMatcher();
        var best = doc.RootElement.EnumerateArray()
            .Where(r => !r.GetProperty("draft").GetBoolean() && !r.GetProperty("prerelease").GetBoolean())
            .Select(r => (Version: int.TryParse(r.GetProperty("tag_name").GetString(), out var v) ? v : -1, Release: r))
            .Where(x => x.Version > 0 && (pinnedVersion == null || x.Version == pinnedVersion))
            .OrderByDescending(x => x.Version)
            .Select(x => (x.Version, Asset: x.Release.GetProperty("assets").EnumerateArray().FirstOrDefault(a => match(a.GetProperty("name").GetString()!))))
            .FirstOrDefault(x => x.Asset.ValueKind == JsonValueKind.Object);
        if (best.Asset.ValueKind != JsonValueKind.Object)
            throw new MemUpdateException("No MassEffectModderNoGui release has a build for this platform");

        if (installed >= best.Version && File.Exists(ExePath))
        {
            status?.Report($"MassEffectModderNoGui {installed} is up to date");
            return ExePath;
        }

        var name = best.Asset.GetProperty("name").GetString()!;
        var size = best.Asset.GetProperty("size").GetInt64();
        var url = best.Asset.GetProperty("browser_download_url").GetString()!;
        status?.Report($"Downloading {name}");

        Directory.CreateDirectory(installDir);
        var work = Path.Combine(installDir, ".update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var archive = Path.Combine(work, name);
            using (var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                r.EnsureSuccessStatusCode();
                await using var src = await r.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(archive);
                await src.CopyToAsync(dst, ct);
            }
            if (new FileInfo(archive).Length != size)
                throw new MemUpdateException($"{name}: downloaded size does not match the release listing");

            status?.Report("Extracting");
            var unpacked = Path.Combine(work, "x");
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ZipFile.ExtractToDirectory(archive, unpacked);
            else await extractor.ExtractAsync(archive, unpacked, ct);

            var found = Directory.EnumerateFiles(unpacked, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == "MassEffectModderNoGui")
                ?? throw new MemUpdateException("MassEffectModderNoGui was not found in the downloaded archive");
            File.Move(found, ExePath, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(ExePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                              | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        finally { try { Directory.Delete(work, recursive: true); } catch (IOException) { } }

        var now = await new MemRunner(runner, ExePath).VersionAsync(ct);
        if (now != best.Version) throw new MemUpdateException($"Installed MEM reports version {now?.ToString() ?? "none"}, expected {best.Version}");
        status?.Report($"MassEffectModderNoGui {now} installed");
        return ExePath;
    }
}
