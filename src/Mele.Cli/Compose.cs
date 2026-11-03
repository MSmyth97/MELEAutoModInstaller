using Mele.Engine.Orchestration;

namespace Mele.Cli;

/// <summary>Maps command-line options onto the shared composition root.</summary>
internal sealed class Compose : IDisposable
{
    private readonly AppServices _services;
    public Orchestrator Orchestrator => _services.Orchestrator;
    public RunOptions Options { get; }

    public Compose(Opts o, IRunHost host)
    {
        var cfg = new AppConfig(
            GameDir: o.GameDir(),
            PrefixDir: o.Get("prefix"), Library: o.Get("library"), CacheDir: o.Get("cache"), MemRoot: o.Get("mem-root"),
            StateFile: o.Get("state"), GuideFile: o.Get("guide"), TexturesFile: o.Get("textures"));
        _services = AppServices.Create(cfg, host);
        var games = o.All("game").Count > 0 ? o.All("game").Select(g => g.ToUpperInvariant()).ToList() : ["LE1", "LE2", "LE3"];
        Options = new RunOptions(games, o.Has("skip-textures"), int.Parse(o.Get("jobs") ?? "3"), o.Has("keep-archives"), double.Parse(o.Get("min-free-gb") ?? "40"));
    }

    public void Dispose() => _services.Dispose();
}
