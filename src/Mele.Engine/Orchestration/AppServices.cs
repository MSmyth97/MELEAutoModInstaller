using Mele.Engine.Archive;
using Mele.Engine.Download;
using Mele.Engine.Mem;
using Mele.Engine.Nexus;
using Mele.Engine.Pipeline;
using Mele.Engine.Platform;
using Mele.Engine.Provision;
using Mele.Engine.Stage;
using Mele.Engine.Textures;

namespace Mele.Engine.Orchestration;

/// <param name="ApiKey">Null = read it from the environment/config file.</param>
public sealed record AppConfig(string GameDir, string? PrefixDir = null, string? Library = null, string? CacheDir = null, string? MemRoot = null,
    string? StateFile = null, string? GuideFile = null, string? TexturesFile = null, string? ApiKey = null);

/// <summary>The real object graph behind the orchestrator. The CLI and the GUI both build it here, so they behave identically.</summary>
public sealed class AppServices(Orchestrator orchestrator, RunPaths paths, M3Location m3, NexusClient nexus, params IDisposable[] owned) : IDisposable
{
    public Orchestrator Orchestrator { get; } = orchestrator;
    public RunPaths Paths { get; } = paths;
    public M3Location M3 { get; } = m3;
    public NexusClient Nexus { get; } = nexus;

    public static AppServices Create(AppConfig cfg, IRunHost host)
    {
        var api = new HttpClient();
        var cdn = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // downloads can be many GB
        try
        {
            var m3 = M3Location.Resolve(cfg.PrefixDir);
            var guide = cfg.GuideFile != null ? GuideConfig.Load(cfg.GuideFile) : GuideConfig.LoadDefault();
            var manifest = cfg.TexturesFile != null ? TextureManifest.Load(cfg.TexturesFile) : TextureManifest.LoadDefault();
            var cache = cfg.CacheDir ?? AppPaths.CacheDir;
            var paths = new RunPaths(
                Library: cfg.Library ?? m3.Library,
                CacheDir: cache,
                MemRoot: cfg.MemRoot ?? Path.Combine(AppPaths.DataDir, "mem-files"),
                GameRoot: cfg.GameDir,
                StateFile: cfg.StateFile ?? Path.Combine(AppPaths.DataDir, "state.json"));

            var nexus = new NexusClient(api, cfg.ApiKey ?? ApiKeyStore.Load());
            var downloader = new Downloader(nexus, cdn, cache);
            var extractor = new SevenZipExtractor();
            var stager = new Stager(extractor);
            var runner = new ProcessRunner();
            var pipeline = new TexturePipeline(downloader, new MonoTorrentFetcher(), stager, extractor, manifest);

            var orchestrator = new Orchestrator(
                new NexusGuideSource(nexus, downloader, extractor, guide),
                new ContentInstaller(downloader, stager),
                new TextureStage(pipeline, manifest, cache),
                async ct => new MemRunner(runner, await new MemUpdater(api, extractor, runner, AppPaths.MemDir).EnsureAsync(null, ct: ct)),
                m3.Backups, nexus.ValidateAsync, guide, paths, host,
                extractorAvailable: () => SevenZipExtractor.FindExecutable() != null || (!OperatingSystem.IsWindows() && Platform.ToolFinder.OnPath("bsdtar")));
            return new AppServices(orchestrator, paths, m3, nexus, api, cdn);
        }
        catch { api.Dispose(); cdn.Dispose(); throw; }
    }

    public void Dispose() { foreach (var d in owned) d.Dispose(); }
}
