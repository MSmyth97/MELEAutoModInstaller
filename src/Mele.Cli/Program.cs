using Mele.Cli;
using Mele.Engine.Archive;
using Mele.Engine.Biq;
using Mele.Engine.Download;
using Mele.Engine.Mem;
using Mele.Engine.Nexus;
using Mele.Engine.Pipeline;
using Mele.Engine.Platform;
using Mele.Engine.Stage;
using Mele.Engine.Textures;

const string Usage = """
    mele <command> [options]

    check    <group.biq2>... [--library DIR] [--link "Mod=URL"]   survey a library (no network when --library is given), or validate the API key
    install  <group.biq2>... --library DIR [--cache DIR] [--jobs N] [--only TEXT] [--link "Mod=URL"] [--keep-archives] [--dry-run]
    textures <textures.json> --library DIR [--mem-root DIR] [--cache DIR] [--game LE1] [--only TEXT] [--keep-archives]
    backups  [--prefix DIR]                                        show M3's vanilla backups (--prefix = Wine/Proton prefix, Linux)
    mem update
    mem install --game LE1|LE2|LE3 --mfl FILE --game-dir DIR [--prefix DIR]   installs textures; refuses without valid backups

    The Nexus key comes from NEXUS_API_KEY or a 0600 file (see README). It is never printed.
    """;

try
{
    var o = new Opts(args);
    if (o.Positional.Count == 0 || o.Has("help")) { Console.WriteLine(Usage); return 0; }
    var cmd = o.Positional[0];
    var rest = o.Positional.Skip(1).ToList();
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    return cmd switch
    {
        "check" => await Check(o, rest),
        "install" => await Install(o, rest, cts.Token),
        "textures" => await Textures(o, rest, cts.Token),
        "backups" => Backups(o),
        "mem" => await Mem(o, rest, cts.Token),
        _ => Fail($"unknown command '{cmd}'\n\n{Usage}"),
    };
}
catch (Exception e) when (e is ArgumentException or ApiKeyException or NexusException or MemUpdateException or FileNotFoundException or InvalidDataException or OperationCanceledException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

static int Fail(string m) { Console.Error.WriteLine(m); return 2; }

static IReadOnlyList<BiqGroup> LoadGroups(Opts o, IEnumerable<string> files)
{
    var links = o.All("link").Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
    var groups = files.Select(f => BiqGroup.Load(f).WithLinks(links)).ToList();
    return groups.Count > 0 ? groups : throw new ArgumentException("give at least one .biq2 group file");
}

static NexusClient NewNexus(HttpClient http) => new(http, ApiKeyStore.Load());

static async Task<int> Check(Opts o, List<string> files)
{
    var groups = LoadGroups(o, files);
    var library = o.Get("library");
    if (library == null)
    {
        var me = await NewNexus(new HttpClient()).ValidateAsync();
        Console.WriteLine($"API key OK for {me.Name} (premium: {me.IsPremium})");
        foreach (var g in groups) Console.WriteLine($"{g.Game}: {g.Mods.Count} mods, {g.Mods.Count(m => m.Downloadable)} downloadable, {g.Mods.Count(m => !m.Downloadable)} need a manual download");
        return me.IsPremium ? 0 : 1;
    }
    int notStaged = 0;
    foreach (var g in groups)
    {
        var s = ContentInstaller.Survey(g, library);
        Console.WriteLine($"{g.Game}: {s.Count(x => x.Status == ContentStatus.AlreadyStaged)} staged, {s.Count(x => x.Status == ContentStatus.WouldInstall)} would install, {s.Count(x => x.Status == ContentStatus.NeedsManualDownload)} manual");
        foreach (var x in s.Where(x => x.Status != ContentStatus.AlreadyStaged)) Console.WriteLine($"  [{x.Status}] {x.Mod.Name}: {x.Note}");
        notStaged += s.Count(x => x.Status == ContentStatus.WouldInstall);
    }
    return notStaged == 0 ? 0 : 1;
}

static async Task<int> Install(Opts o, List<string> files, CancellationToken ct)
{
    var groups = LoadGroups(o, files);
    var library = o.Require("library");
    var only = o.Get("only");
    Func<ModEntry, bool>? filter = only == null ? null : m => m.Name.Contains(only, StringComparison.OrdinalIgnoreCase);
    int failed = 0;
    if (o.Has("dry-run"))
    {
        foreach (var g in groups)
        {
            Console.WriteLine($"== {g.Game} (dry run: nothing is downloaded or changed)");
            foreach (var r in ContentInstaller.Survey(g, library).Where(r => filter?.Invoke(r.Mod) ?? true))
                Console.WriteLine($"  [{r.Status}] {r.Mod.Name}: {r.Note}");
        }
        return 0;
    }
    using var http = new HttpClient();
    using var cdn = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    var downloader = new Downloader(NewNexus(http), cdn, o.Get("cache") ?? AppPaths.CacheDir);
    var installer = new ContentInstaller(downloader, new Stager(new SevenZipExtractor()));
    foreach (var g in groups)
    {
        Console.WriteLine($"== {g.Game}: {g.Mods.Count(m => filter?.Invoke(m) ?? true)} mods");
        var results = await installer.InstallAsync(g, library, int.Parse(o.Get("jobs") ?? "3"), o.Has("keep-archives"), filter,
            r => Console.WriteLine($"  [{r.Status}] {r.Mod.Name}: {r.Note}"), ct);
        failed += results.Count(r => r.Status == ContentStatus.Failed);
    }
    Console.WriteLine(failed == 0 ? "\nDone. In M3: reload the library, then Batch Mod Installer > load the .biq2 > Install."
                                  : $"\n{failed} mod(s) failed; fix and re-run (finished mods are skipped).");
    return failed == 0 ? 0 : 1;
}

static async Task<int> Textures(Opts o, List<string> files, CancellationToken ct)
{
    var manifest = TextureManifest.Load(files.Count == 1 ? files[0] : throw new ArgumentException("give exactly one textures.json"));
    var library = o.Require("library");
    var cache = o.Get("cache") ?? AppPaths.CacheDir;
    var memRoot = o.Get("mem-root") ?? Path.Combine(AppPaths.DataDir, "mem-files");
    var only = o.Get("only");
    using var http = new HttpClient();
    using var cdn = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    var extractor = new SevenZipExtractor();
    var pipeline = new TexturePipeline(new Downloader(NewNexus(http), cdn, cache), new MonoTorrentFetcher(), new Stager(extractor), extractor, manifest);

    int failed = 0;
    foreach (var game in o.All("game").DefaultIfEmpty("").SelectMany(g => g == "" ? ["LE1", "LE2", "LE3"] : new[] { g }))
    {
        Console.WriteLine($"== {game} textures");
        int gameFailed = 0, i = 0;
        foreach (var item in manifest.Items(game))
        {
            i++;
            if (only != null && !item.Name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
            var lastPct = -10.0;
            var progress = new Progress<double>(p => { if (p - lastPct >= 10) { lastPct = p; Console.WriteLine($"    {item.Name}: {p:0}%"); } });
            var r = await pipeline.ProcessAsync(game, i, item, library, memRoot, Path.Combine(cache, "torrent"), o.Has("keep-archives"), progress, ct);
            Console.WriteLine($"  [{r.Status}] {i}. {r.Name}: {r.Note}");
            if (r.Status == TextureStatus.Failed) gameFailed++;
        }
        failed += gameFailed;
        if (gameFailed == 0 && only == null)
        {
            var (batch, mfl) = pipeline.WriteOutputs(game, library, memRoot, Path.Combine(memRoot, $"{game}.mfl"));
            Console.WriteLine($"  wrote {batch}\n  wrote {mfl}");
        }
    }
    return failed == 0 ? 0 : 1;
}

static IBackupLocator Locator(Opts o)
{
    if (OperatingSystem.IsWindows()) return new RegistryBackupLocator();
    var prefix = WinePrefix.Find(o.Get("prefix") ?? throw new ArgumentException("--prefix (the Wine/Proton prefix M3 runs in) is required on this OS"))
                 ?? throw new ArgumentException("no Wine prefix (user.reg) found at --prefix");
    return new WinePrefixBackupLocator(prefix);
}

static int Backups(Opts o)
{
    var loc = Locator(o);
    bool all = true;
    foreach (var g in new[] { "LE1", "LE2", "LE3" })
    {
        var b = loc.Get(g);
        Console.WriteLine($"{g}: {(b.Valid ? "OK " : "BAD")} {b.Path ?? "-"}  ({b.Reason})");
        all &= b.Valid;
    }
    return all ? 0 : 1;
}

static async Task<int> Mem(Opts o, List<string> sub, CancellationToken ct)
{
    var runner = new ProcessRunner();
    using var http = new HttpClient();
    var updater = new MemUpdater(http, new SevenZipExtractor(), runner, AppPaths.MemDir);
    var status = new Progress<string>(Console.WriteLine);
    switch (sub.FirstOrDefault())
    {
        case "update":
            await updater.EnsureAsync(status, ct: ct);
            return 0;
        case "install":
            var game = o.Require("game");
            var backup = Locator(o).Get(game);
            if (!backup.Valid)
            {
                Console.Error.WriteLine($"Refusing to install textures into {game}: no valid M3 backup ({backup.Reason}). Make a backup in Mod Manager first.");
                return 3;
            }
            Console.WriteLine($"Backup OK: {backup.Path}");
            var mem = new MemRunner(runner, await updater.EnsureAsync(status, ct: ct));
            if (!await mem.SetGamePathAsync(game, o.Require("game-dir"), ct)) { Console.Error.WriteLine("MEM would not accept the game path"); return 1; }
            int packages = 0;
            var result = await mem.InstallAsync(game, Path.GetFullPath(o.Require("mfl")), line =>
            {
                if (line.StartsWith("Package", StringComparison.Ordinal)) { if (++packages % 500 == 0) Console.WriteLine($"  ...{packages} package lines"); }
                else if (line.Length > 0) Console.WriteLine("  " + line);
            }, ct: ct);
            Console.WriteLine(result.Success ? $"{game}: textures installed." : $"{game}: FAILED (exit {result.ExitCode}); see MEM output above.");
            return result.Success ? 0 : 1;
        default:
            return Fail("mem needs a subcommand: update | install");
    }
}
