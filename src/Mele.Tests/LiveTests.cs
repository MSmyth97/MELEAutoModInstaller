using System.IO.Compression;
using Mele.Engine.Archive;
using Mele.Engine.Download;
using Mele.Engine.Mem;
using Mele.Engine.Platform;
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

    private static string PrefixPath => Environment.GetEnvironmentVariable("MELE_PREFIX")
        ?? "/home/matt/.local/share/Steam/steamapps/compatdata/2199358105";
    private static string GameRoot => Environment.GetEnvironmentVariable("MELE_GAME")
        ?? "/home/matt/Games/Mass Effect Legendary Edition";

    [LiveFact]
    public void Backup_locator_finds_the_real_m3_backups()
    {
        var prefix = WinePrefix.Find(PrefixPath) ?? throw new DirectoryNotFoundException(PrefixPath);
        var loc = new WinePrefixBackupLocator(prefix);
        foreach (var g in new[] { "LE1", "LE2", "LE3" })
        {
            var b = loc.Get(g);
            Console.WriteLine($"{g}: {b.Path} -> {b.Reason}");
            Assert.True(b.Valid, $"{g}: {b.Reason}");
        }
    }

    [LiveFact]
    public async Task Native_mem_downloads_runs_and_reads_the_game_read_only()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var runner = new ProcessRunner();
        // Isolated config dir: MEM writes its game path to ~/.config; don't touch the user's real one.
        var env = new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = Path.Combine(tmp.Path, "cfg") };

        var exe = await new MemUpdater(new HttpClient(), new SevenZipExtractor(), runner, Path.Combine(tmp.Path, "mem"))
            .EnsureAsync(new Progress<string>(Console.WriteLine));
        var mem = new MemRunner(runner, exe, env);
        var version = await mem.VersionAsync();
        Console.WriteLine($"MEM version {version}");
        Assert.True(version > 500);

        Assert.True(await mem.SetGamePathAsync("LE1", GameRoot));
        var lines = new List<string>();
        Assert.Equal(0, await runner.RunAsync(exe, ["--get-game-paths"], lines.Add, env));
        Assert.Contains(lines, l => l.Contains(GameRoot));

        // read-only: lists files without texture markers; installs nothing
        var check = new List<string>();
        var rc = await runner.RunAsync(exe, ["--check-game-data-after", "--gameid", "1"], check.Add, env);
        Console.WriteLine($"check-game-data-after exit={rc}, {check.Count} lines, last: {check.LastOrDefault(l => l.Length > 0)}");
        Assert.Equal(0, rc);
        Assert.Contains(check, l => l.Contains("Finished checking"));
    }

    [LiveFact]
    public void Generated_batch_groups_match_the_ones_the_python_tool_made_for_the_same_library()
    {
        var library = Environment.GetEnvironmentVariable("MELE_LIBRARY")
            ?? "/home/matt/.local/share/Steam/steamapps/compatdata/2199358105/pfx/drive_c/users/matt/Documents/ME3TweaksModManager/mods";
        var manifestPath = Path.Combine(Path.GetDirectoryName(Repo.Samples)!, "textures.json");
        var manifest = TextureManifest.Load(manifestPath);
        using var tmp = new TempDir();
        foreach (var game in new[] { "LE1", "LE2", "LE3" })
        {
            var mine = Mele.Engine.Biq.BiqGroup.Load(M3toBatchWriter.Write(manifest, game, library, tmp.Path));
            var theirs = Mele.Engine.Biq.BiqGroup.Load(Path.Combine(library, "BatchModQueues", $"{game} Textures M3TO (generated).biq2"));
            Assert.Equal(theirs.Mods.Select(m => (m.Name, m.ModDescRelPath, m.ModDescMd5, m.ModDescSize, m.ModId, m.FileId)),
                         mine.Mods.Select(m => (m.Name, m.ModDescRelPath, m.ModDescMd5, m.ModDescSize, m.ModId, m.FileId)));
        }
    }
}
