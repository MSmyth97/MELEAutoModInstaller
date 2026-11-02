using Mele.Engine.Archive;
using Mele.Engine.Download;
using Mele.Engine.Mem;
using Mele.Engine.Nexus;
using Mele.Engine.Orchestration;
using Mele.Engine.Pipeline;
using Mele.Engine.Platform;
using Mele.Engine.Provision;
using Mele.Engine.Stage;
using Mele.Engine.Textures;

namespace Mele.Cli;

/// <summary>Builds the real services for `plan` and `run`. The GUI will have its own composition root over the same engine.</summary>
internal sealed class Compose : IDisposable
{
    private readonly HttpClient _api = new();
    private readonly HttpClient _cdn = new() { Timeout = Timeout.InfiniteTimeSpan };
    public Orchestrator Orchestrator { get; }
    public RunOptions Options { get; }

    public Compose(Opts o, IRunHost host)
    {
        // Where is Mod Manager's library? On Linux it lives in a Wine prefix (--prefix, default: the app's own).
        string m3Home;
        IBackupLocator backups;
        if (OperatingSystem.IsWindows())
        {
            m3Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ME3TweaksModManager");
            backups = new RegistryBackupLocator();
        }
        else
        {
            var prefix = new ProtonPrefix(o.Get("prefix") ?? ProtonPrefix.DefaultRoot, new ProcessRunner());
            if (WinePrefix.Find(prefix.Root) is not { } wine) throw new ArgumentException($"No Wine prefix at {prefix.Root}. Use --prefix, or run: mele m3 setup");
            prefix = new ProtonPrefix(wine.Root, new ProcessRunner()); // normalise "…/compatdata/ID" to the prefix itself
            m3Home = prefix.M3Home;
            backups = new WinePrefixBackupLocator(wine);
        }

        var repo = FindRepoRoot();
        var guide = GuideConfig.Load(o.Get("guide") ?? Path.Combine(repo, "guide.json"));
        var manifest = TextureManifest.Load(o.Get("textures") ?? Path.Combine(repo, "textures.json"));
        var cache = o.Get("cache") ?? AppPaths.CacheDir;
        var paths = new RunPaths(
            Library: o.Get("library") ?? Path.Combine(m3Home, "mods"),
            CacheDir: cache,
            MemRoot: o.Get("mem-root") ?? Path.Combine(AppPaths.DataDir, "mem-files"),
            GameRoot: o.Get("game-dir") ?? throw new ArgumentException("--game-dir (the 'Mass Effect Legendary Edition' folder) is required"),
            StateFile: o.Get("state") ?? Path.Combine(AppPaths.DataDir, "state.json"));

        var nexus = new NexusClient(_api, ApiKeyStore.Load());
        var downloader = new Downloader(nexus, _cdn, cache);
        var extractor = new SevenZipExtractor();
        var stager = new Stager(extractor);
        var runner = new ProcessRunner();
        var pipeline = new TexturePipeline(downloader, new MonoTorrentFetcher(), stager, extractor, manifest);

        Orchestrator = new Orchestrator(
            new NexusGuideSource(nexus, downloader, extractor, guide),
            new ContentInstaller(downloader, stager),
            new TextureStage(pipeline, manifest, cache),
            async ct => new MemRunner(runner, await new MemUpdater(_api, extractor, runner, AppPaths.MemDir).EnsureAsync(null, ct: ct)),
            backups,
            nexus.ValidateAsync,
            guide, paths, host);

        var games = o.All("game").Count > 0 ? o.All("game").Select(g => g.ToUpperInvariant()).ToList() : ["LE1", "LE2", "LE3"];
        Options = new RunOptions(games, o.Has("skip-textures"), int.Parse(o.Get("jobs") ?? "3"), o.Has("keep-archives"), double.Parse(o.Get("min-free-gb") ?? "40"));
    }

    private static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "guide.json"))) return d.FullName;
        return Directory.GetCurrentDirectory();
    }

    public void Dispose() { _api.Dispose(); _cdn.Dispose(); }
}
