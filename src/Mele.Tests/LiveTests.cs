using System.IO.Compression;
using Mele.Engine.Archive;
using Mele.Engine.Download;
using Mele.Engine.Nexus;
using Mele.Engine.Textures;

namespace Mele.Tests;

/// <summary>Skipped unless MELE_LIVE=1 (needs a Premium Nexus key in NEXUS_API_KEY or ~/.config/mele-auto-mod-installer/api_key).</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute() { if (Environment.GetEnvironmentVariable("MELE_LIVE") != "1") Skip = "set MELE_LIVE=1 to run live tests"; }
}

public class LiveTests
{
    private static string ApiKey()
    {
        var k = Environment.GetEnvironmentVariable("NEXUS_API_KEY");
        if (!string.IsNullOrWhiteSpace(k)) return k.Trim();
        var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "mele-auto-mod-installer", "api_key");
        return File.ReadAllText(f).Trim();
    }

    [LiveFact]
    public async Task Torrent_member_is_identical_to_the_direct_nexus_download()
    {
        using var tmp = new TempDir();
        using var http = new HttpClient();
        var nexus = new NexusClient(http, ApiKey());
        Assert.True((await nexus.ValidateAsync()).IsPremium);
        var downloader = new Downloader(nexus, new HttpClient(), Path.Combine(tmp.Path, "cache"));

        // .torrent from Nexus (file 12950 is a zip holding it)
        var (zip, _) = await downloader.FetchAsync("masseffectlegendaryedition", 363, 12950);
        var torrentFile = Path.Combine(tmp.Path, "t.torrent");
        using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
            z.Entries.Single(e => e.Name == "ALOT_ISL_M3TO.torrent").ExtractToFile(torrentFile);

        var viaTorrent = await new MonoTorrentFetcher().FetchMemberAsync(torrentFile, "ALotofTextures(ALOT)forLE1_2021.1.3.7z",
            Path.Combine(tmp.Path, "torrent"), new Progress<double>(p => Console.WriteLine($"torrent {p:0}%")));
        var (direct, _) = await downloader.FetchAsync("masseffectlegendaryedition", 363, 13763); // ALOT for ME1 1.4 (M3TO)

        // The torrent member and Nexus' direct file are different .7z builds (sizes differ by a byte), so compare
        // what matters: extracting both must succeed (archive CRCs, which a sparse/partial file would fail) and
        // yield identical files.
        var ex = new SevenZipExtractor();
        await ex.ExtractAsync(viaTorrent, Path.Combine(tmp.Path, "t"));
        await ex.ExtractAsync(direct, Path.Combine(tmp.Path, "d"));
        var t = await Tree(Path.Combine(tmp.Path, "t"));
        var d = await Tree(Path.Combine(tmp.Path, "d"));
        Assert.NotEmpty(t);
        Assert.Contains(t.Keys, k => k.EndsWith("moddesc.ini", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(d.Keys, t.Keys);
        var differing = d.Keys.Where(k => d[k] != t[k]).ToList();
        Console.WriteLine("files that differ between the torrent build and Nexus' direct build: " + string.Join(", ", differing));
        // The two builds may differ in small config files; the texture payload must be identical.
        Assert.All(differing, k => Assert.EndsWith(".ini", k, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<SortedDictionary<string, string>> Tree(string root)
    {
        var r = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            r[Path.GetRelativePath(root, f).Replace('\\', '/')] = await Downloader.Md5Async(f);
        return r;
    }
}
