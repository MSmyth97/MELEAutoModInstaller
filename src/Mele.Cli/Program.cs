using Mele.Engine.Backup;
using Mele.Cli;
using Mele.Engine.Archive;
using Mele.Engine.Biq;
using Mele.Engine.Download;
using Mele.Engine.Mem;
using Mele.Engine.Nexus;
using Mele.Engine.Orchestration;
using Mele.Engine.Pipeline;
using Mele.Engine.Platform;
using Mele.Engine.Provision;
using Mele.Engine.Stage;
using Mele.Engine.Textures;

const string Usage = """
    mele <command> [options]

    detect   find the Mass Effect Legendary Edition install(s) on this machine (Steam, Heroic, Epic, EA, and the usual game folders)
    plan     --game-dir DIR [--prefix DIR] [--game LE1]...        read-only: checks, the guide's current groups, and what a run would do
    run      --game-dir DIR [--prefix DIR] [--game LE1]... [--skip-textures] [--yes] [--keep-archives] [--min-free-gb N]
             the whole guide: mods -> textures -> (you install in Mod Manager) -> MEM. Resumable; backups required.
    state    show|reset|adopt [--game LE1]... [--state FILE]      what the app believes is done. reset = start a fresh playthrough
             (do this after restoring backups); adopt = record that a game is already fully modded (installed by hand or by an older version)
    check    <group.biq2>... [--library DIR] [--link "Mod=URL"]   survey a library (no network when --library is given), or validate the API key
    install  <group.biq2>... --library DIR [--cache DIR] [--jobs N] [--only TEXT] [--link "Mod=URL"] [--keep-archives] [--dry-run]
    textures <textures.json> --library DIR [--mem-root DIR] [--cache DIR] [--game LE1] [--only TEXT] [--keep-archives]
    backups  [--prefix DIR]                                        show M3's vanilla backups (--prefix = Wine/Proton prefix, Linux)
    backups compare --game LE1 --game-dir DIR [--prefix DIR] [--list N]   read-only: game folder vs its backup, and whether the game looks vanilla
    backups create --game LE1 --game-dir DIR --prefix DIR [--to DIR] [--dry-run] [--yes] [--replace]
             copy a VANILLA game to a backup folder (default: ME1 Backup next to the game folder), verify it, and tell Mod Manager about it.
             Refuses a modded game, a non-empty destination, too little free space, or a running game.
    backups restore --game LE1 --game-dir DIR [--prefix DIR] [--dry-run] [--yes] [--delete-unknown] [--list N]
             put the game back to its backup: copies what differs, removes mod files, keeps vanilla files the backup lacks (other languages).
             Always shows the plan first; --dry-run stops there. Refuses while a Mass Effect game is running.
    m3 setup  [--prefix-dir DIR]   download Mod Manager (and, on Linux, create the app's own Proton prefix for it)
    m3 status [--prefix-dir DIR]
    m3 launch [--prefix-dir DIR]
    m3 targets --game-dir DIR [--game LE1]... [--prefix-dir DIR]   read-only: does Mod Manager have your game folder as a target? (shows the path to give it)
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
        "detect" => Detect(),
        "plan" => await Plan(o, cts.Token),
        "state" => StateCmd(o, rest),
        "run" => await Run(o, cts.Token),
        "install" => await Install(o, rest, cts.Token),
        "textures" => await Textures(o, rest, cts.Token),
        "backups" => await BackupsAsync(o, rest, cts.Token),
        "mem" => await Mem(o, rest, cts.Token),
        "m3" => await M3(o, rest, cts.Token),
        _ => Fail($"unknown command '{cmd}'\n\n{Usage}"),
    };
}
catch (Exception e) when (e is RestoreException or BackupException or ProvisionException or IOException or ArgumentException or ApiKeyException or NexusException or MemUpdateException or FileNotFoundException or InvalidDataException or OperationCanceledException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

static int Detect()
{
    var found = GameLocator.Default().Find();
    if (found.Count == 0)
    {
        Console.WriteLine("No Mass Effect Legendary Edition install was found. Looked in the Steam, Heroic and Epic libraries and the usual game folders;");
        Console.WriteLine("give the folder with --game-dir (the one that contains 'Game', 'Launcher' and 'Support').");
        return 1;
    }
    foreach (var f in found) Console.WriteLine($"{f.Path}\n    found in: {f.Source}   games: {string.Join(", ", f.Games)}");
    return 0;
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

static int BackupsCompare(Opts o)
{
    var game = o.Require("game");
    var folder = GameFolders.Root(o.GameDir(), game);
    var b = Locator(o).Get(game);
    if (!b.Valid) { Console.Error.WriteLine($"{game}: no valid backup ({b.Reason})"); return 1; }
    if (!Directory.Exists(folder)) { Console.Error.WriteLine($"{game}: game folder not found ({folder})"); return 1; }

    var report = GameReport.Build(game, folder, b);
    Console.WriteLine($"{game} game:   {folder}\n{game} backup: {b.Path}");
    foreach (var line in report.Lines()) Console.WriteLine(line);
    var r = report.Compare!;
    var list = int.TryParse(o.Get("list"), out var n2) ? n2 : 0;
    if (list > 0)
    {
        foreach (var m in r.Missing.Take(list)) Console.WriteLine($"  missing  {m}");
        foreach (var d in r.Different.Take(list)) Console.WriteLine($"  differs  {d.Path} (backup {d.BackupSize}, game {d.GameSize})");
        foreach (var e in r.Extra.Take(list)) Console.WriteLine($"  extra    [{e.Kind}] {e.Path}");
    }
    return report.Clean ? 0 : 1;
}

static async Task<int> BackupsRestore(Opts o, CancellationToken ct)
{
    var game = o.Require("game");
    var folder = GameFolders.Root(o.GameDir(), game);
    var b = Locator(o).Get(game);
    var restorer = new GameRestorer();
    var plan = restorer.Prepare(game, b, folder, o.Has("delete-unknown"), ct);

    Console.WriteLine($"{game} game:   {folder}\n{game} backup: {plan.BackupRoot}");
    foreach (var line in plan.Describe()) Console.WriteLine($"  {line}");
    var list = int.TryParse(o.Get("list"), out var n) ? n : 0;
    foreach (var p in plan.Copy.Take(list)) Console.WriteLine($"  copy    {p}");
    foreach (var p in plan.Delete.Take(list)) Console.WriteLine($"  delete  {p}");
    foreach (var p in plan.KeptUnknown.Take(list)) Console.WriteLine($"  keep?   {p}");

    if (plan.NothingToDo) { Console.WriteLine("Nothing to do: the game already matches its backup."); return 0; }
    if (o.Has("dry-run")) { Console.WriteLine("Dry run; nothing was changed."); return 0; }
    if (!o.Has("yes"))
    {
        Console.Write($"This changes files in {folder}. Continue? [y/N] ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Cancelled; nothing was changed."); return 1; }
    }

    var last = "";
    var progress = new Progress<FileProgress>(p =>
    {
        if (p.Phase != last) { last = p.Phase; Console.WriteLine($"{p.Phase}..."); }
        else if (p.Total > 0 && p.Done > 0 && p.Done % 500 == 0) Console.WriteLine($"  {p.Done}/{p.Total}");
    });
    var result = await restorer.RestoreAsync(plan, progress, ct);
    await Task.Delay(50, CancellationToken.None);   // let the last progress lines print
    Console.WriteLine($"Restored {result.Copied} file(s), removed {result.Deleted}.");
    foreach (var problem in result.Problems) Console.Error.WriteLine($"problem: {problem}");
    Console.WriteLine(result.Success ? "The game now matches its backup." : "The restore did not finish cleanly; see the problems above.");
    return result.Success ? 0 : 1;
}

static IBackupRegistry BackupRegistry(Opts o)
{
    if (OperatingSystem.IsWindows()) return new WindowsBackupRegistry();
    var wine = WinePrefix.Find(o.Get("prefix") ?? throw new ArgumentException("--prefix (the Wine/Proton prefix M3 runs in) is required on this OS"))
               ?? throw new ArgumentException("no Wine prefix (user.reg) found at --prefix");
    return new WineBackupRegistry(new ProtonPrefix(wine.Root, new ProcessRunner()));
}

static async Task<int> BackupsCreate(Opts o, CancellationToken ct)
{
    var game = o.Require("game");
    var gameDir = o.GameDir();
    var folder = GameFolders.Root(gameDir, game);
    var dest = o.Get("to") ?? BackupCreator.DefaultDestination(gameDir, game);
    var creator = new BackupCreator(BackupRegistry(o));
    var plan = creator.Prepare(game, folder, dest, Locator(o).Get(game), o.Has("replace"), ct);

    Console.WriteLine($"{game} game:   {plan.GameFolder}\n{game} backup: {plan.Destination}");
    Console.WriteLine($"  Copy {plan.Files} file(s), {plan.Bytes / 1073741824.0:0.0} GB, then verify and register the backup with Mod Manager");
    if (o.Has("dry-run")) { Console.WriteLine("Dry run; nothing was copied."); return 0; }
    if (!o.Has("yes"))
    {
        Console.Write("Continue? [y/N] ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Cancelled; nothing was copied."); return 1; }
    }
    var last = "";
    var progress = new Progress<FileProgress>(p =>
    {
        if (p.Phase != last) { last = p.Phase; Console.WriteLine($"{p.Phase}..."); }
        else if (p.Total > 0 && p.Done > 0 && p.Done % 1000 == 0) Console.WriteLine($"  {p.Done}/{p.Total}");
    });
    var result = await creator.CreateAsync(plan, progress, ct);
    await Task.Delay(50, CancellationToken.None);
    foreach (var problem in result.Problems) Console.Error.WriteLine($"problem: {problem}");
    Console.WriteLine(result.Success ? (result.Registered ? "Backup created, verified and registered with Mod Manager." : "Backup created and verified, but Mod Manager still needs to be told about it (see above).") : "The backup was not created.");
    return result.Success && result.Registered ? 0 : 1;
}

static async Task<int> BackupsAsync(Opts o, List<string> sub, CancellationToken ct) => sub.FirstOrDefault() switch
{
    "restore" => await BackupsRestore(o, ct),
    "create" => await BackupsCreate(o, ct),
    _ => Backups(o, sub),
};

static int Backups(Opts o, List<string> sub)
{
    if (sub.FirstOrDefault() == "compare") return BackupsCompare(o);
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
            if (!await mem.SetGamePathAsync(game, o.GameDir(), ct)) { Console.Error.WriteLine("MEM would not accept the game path"); return 1; }
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

static (ProtonPrefix? Prefix, string M3Home) M3Location(Opts o)
{
    if (OperatingSystem.IsWindows())
        return (null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ME3TweaksModManager"));
    var prefix = new ProtonPrefix(o.Get("prefix-dir") ?? ProtonPrefix.DefaultRoot, new ProcessRunner());
    return (prefix, prefix.M3Home);
}

static async Task<int> M3(Opts o, List<string> sub, CancellationToken ct)
{
    var (prefix, home) = M3Location(o);
    var status = new Progress<string>(Console.WriteLine);
    using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    var m3 = new M3Installer(http, new SevenZipExtractor(), home);
    switch (sub.FirstOrDefault())
    {
        case "setup":
            if (prefix != null)
            {
                Console.WriteLine($"Proton prefix: {prefix.Root}");
                await prefix.EnsureAsync(null, ct);
                m3 = new M3Installer(http, new SevenZipExtractor(), prefix.M3Home); // the profile folder exists only after the prefix does
            }
            await m3.EnsureAsync(status, ct);
            Console.WriteLine($"Mod Manager: {m3.ExePath}\nMod library: {m3.LibraryPath}");
            if (prefix != null) Console.WriteLine("In Mod Manager, point it at your game with the Z: drive, e.g. Z:\\home\\you\\Games\\Mass Effect Legendary Edition");
            return 0;
        case "targets":
        {
            var gameDir = o.GameDir();
            var loc = Mele.Engine.Provision.M3Location.Resolve(o.Get("prefix-dir"));
            var games = o.All("game").Count > 0 ? o.All("game") : ["LE1", "LE2", "LE3"];
            var bad = 0;
            foreach (var g in games)
            {
                var c = loc.Targets.Check(g, GameFolders.Root(gameDir, g));
                if (c == null) { Console.WriteLine($"{g}: Mod Manager has not been set up here yet"); bad++; continue; }
                Console.WriteLine($"{g}: {(c.Ok ? "OK " : "BAD")} {c.Message}");
                if (!c.Ok) bad++;
            }
            return bad == 0 ? 0 : 1;
        }
        case "status":
            Console.WriteLine($"Mod Manager: {(m3.IsInstalled ? m3.ExePath : "not installed")}");
            if (prefix != null) Console.WriteLine($"Prefix: {prefix.Root} ({(prefix.IsInitialized ? "ready" : "not created")}), umu-run: {ProtonPrefix.FindUmu() ?? "NOT FOUND"}");
            return m3.IsInstalled ? 0 : 1;
        case "launch":
            if (!m3.IsInstalled) return Fail("Mod Manager is not installed; run: mele m3 setup");
            if (prefix == null)
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(m3.ExePath) { UseShellExecute = true })!;
                await p.WaitForExitAsync(ct);
            }
            else
            {
                using var p = prefix.LaunchM3(m3.ExePath);
                await p.WaitForExitAsync(ct);
            }
            return 0;
        default:
            return Fail("m3 needs a subcommand: setup | status | launch");
    }
}

static async Task<int> Plan(Opts o, CancellationToken ct)
{
    using var c = new Compose(o, new ConsoleHost(false));
    var plan = await c.Orchestrator.PlanAsync(c.Options, ct);
    foreach (var i in plan.Issues) Console.WriteLine($"{(i.Blocking ? "BLOCKING" : "warning ")} {i.Message}");
    if (plan.Issues.Any(i => i.Blocking)) { Console.WriteLine("\nA run would stop at these checks; nothing has been changed."); return 1; }
    foreach (var g in plan.Games)
        Console.WriteLine($"{g.Game}: mods {g.Staged} staged / {g.ToInstall} to download / {g.Manual} manual; "
                          + $"textures prepared={g.State.TexturesPrepared}, mod manager confirmed={g.State.M3Confirmed}, textures installed={g.State.TexturesInstalled}"
                          + (g.GuideChanged ? "; GUIDE CHANGED since you last confirmed Mod Manager" : ""));
    Console.WriteLine("\nPlan only; nothing was installed.");
    return 0;
}

static async Task<int> Run(Opts o, CancellationToken ct)
{
    using var c = new Compose(o, new ConsoleHost(o.Has("yes")));
    var result = await c.Orchestrator.RunAsync(c.Options, ct);
    Console.WriteLine($"\nResult: {result.Outcome}");
    foreach (var n in result.Notes) Console.WriteLine($"  - {n}");
    if (result.Outcome == RunOutcome.Completed && result.ManualSteps.Count > 0)
    {
        Console.WriteLine("\nStill to do by hand:");
        foreach (var m in result.ManualSteps) Console.WriteLine($"  - {m}");
    }
    return result.Outcome == RunOutcome.Completed ? 0 : result.Outcome == RunOutcome.PausedAtM3 ? 0 : 1;
}

static int StateCmd(Opts o, List<string> sub)
{
    var file = o.Get("state") ?? Path.Combine(AppPaths.DataDir, "state.json");
    var state = RunState.Load(file);
    var games = o.All("game").Count > 0 ? o.All("game").Select(g => g.ToUpperInvariant()).ToList() : ["LE1", "LE2", "LE3"];
    switch (sub.FirstOrDefault())
    {
        case "show":
            Console.WriteLine($"State file: {file}{(File.Exists(file) ? "" : " (none yet)")}");
            foreach (var g in games)
            {
                var s = state.For(g);
                Console.WriteLine($"{g}: textures prepared={s.TexturesPrepared}, mod manager confirmed={s.M3Confirmed}, textures installed={s.TexturesInstalled}");
            }
            return 0;
        case "reset":
            foreach (var g in games) state.Games.Remove(g);
            state.Save(file);
            Console.WriteLine($"Reset {string.Join(", ", games)}: the next run starts from the beginning.");
            return 0;
        case "adopt":
            foreach (var g in games) { var s = state.For(g); s.TexturesPrepared = s.M3Confirmed = s.TexturesInstalled = true; }
            state.Save(file);
            Console.WriteLine($"Recorded {string.Join(", ", games)} as fully installed. A run will skip them.");
            return 0;
        default:
            return Fail("state needs a subcommand: show | reset | adopt");
    }
}
