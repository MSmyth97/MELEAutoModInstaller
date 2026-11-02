using System.Text.Json;
using Mele.Engine.Archive;

namespace Mele.Engine.Provision;

public sealed class ProvisionException(string message) : Exception(message);

/// <summary>
/// Installs ME3Tweaks Mod Manager by unpacking the official release "Extractor" (a stub with an embedded 7z that holds
/// ME3TweaksModManager.exe) with 7-Zip, so no installer has to run (matters under Wine). An existing install is never overwritten:
/// M3 updates itself and users' libraries live next to it.
/// </summary>
public sealed class M3Installer(HttpClient http, IArchiveExtractor extractor, string m3Home)
{
    private const string ReleasesUrl = "https://api.github.com/repos/ME3Tweaks/ME3TweaksModManager/releases?per_page=10";
    public const string ExeName = "ME3TweaksModManager.exe";

    public string ExePath => Path.Combine(m3Home, ExeName);
    public string LibraryPath => Path.Combine(m3Home, "mods");
    public bool IsInstalled => File.Exists(ExePath);

    public async Task<string> EnsureAsync(IProgress<string>? status = null, CancellationToken ct = default)
    {
        if (IsInstalled)
        {
            status?.Report("Mod Manager is already installed; leaving it as it is");
            return ExePath;
        }

        status?.Report("Checking ME3Tweaks Mod Manager releases");
        using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
        req.Headers.UserAgent.ParseAdd("MELEAutoModInstaller");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        // Releases are listed newest first; take the first stable one that carries the extractor.
        var asset = doc.RootElement.EnumerateArray()
            .Where(r => !r.GetProperty("draft").GetBoolean() && !r.GetProperty("prerelease").GetBoolean())
            .SelectMany(r => r.GetProperty("assets").EnumerateArray())
            .FirstOrDefault(a => a.GetProperty("name").GetString() is { } n
                                 && n.StartsWith("ME3TweaksModManagerExtractor_", StringComparison.Ordinal) && n.EndsWith(".exe", StringComparison.Ordinal));
        if (asset.ValueKind != JsonValueKind.Object) throw new ProvisionException("No Mod Manager release with an extractor was found");

        var name = asset.GetProperty("name").GetString()!;
        var size = asset.GetProperty("size").GetInt64();
        status?.Report($"Downloading {name} ({size / 1048576} MB)");

        Directory.CreateDirectory(m3Home);
        var work = Path.Combine(m3Home, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var archive = Path.Combine(work, name);
            using (var r = await http.GetAsync(asset.GetProperty("browser_download_url").GetString()!, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                r.EnsureSuccessStatusCode();
                await using var src = await r.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(archive);
                await src.CopyToAsync(dst, ct);
            }
            if (new FileInfo(archive).Length != size) throw new ProvisionException($"{name}: downloaded size does not match the release listing");

            status?.Report("Unpacking Mod Manager");
            var unpacked = Path.Combine(work, "x");
            await extractor.ExtractAsync(archive, unpacked, ct);
            var exe = Directory.EnumerateFiles(unpacked, ExeName, SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new ProvisionException($"{ExeName} was not found inside {name}");
            File.Move(exe, ExePath);
        }
        finally { try { Directory.Delete(work, recursive: true); } catch (IOException) { } }

        Directory.CreateDirectory(LibraryPath);
        status?.Report("Mod Manager installed");
        return ExePath;
    }
}
