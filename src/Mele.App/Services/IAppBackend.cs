using Mele.Engine.Orchestration;
using Mele.Engine.Platform;

namespace Mele.App.Services;

public sealed record M3Status(bool Installed, string Path, bool PrefixReady, bool NeedsPrefix, bool UmuMissing);

/// <summary>Everything the window needs from the machine and the engine. The real one talks to the network; tests use a fake.</summary>
public interface IAppBackend
{
    Task<(bool Ok, string Message)> CheckKeyAsync(string key, CancellationToken ct);
    bool HasSavedKey { get; }
    IReadOnlyList<BackupInfo> GetBackups(AppSettings s);
    M3Status GetM3Status(AppSettings s);
    Task SetupM3Async(AppSettings s, IProgress<string> progress, CancellationToken ct);
    void LaunchM3(AppSettings s);
    Task<RunPlan> PlanAsync(AppSettings s, IRunHost host, CancellationToken ct);
    Task<RunResult> RunAsync(AppSettings s, IRunHost host, CancellationToken ct);
    void ResetState();
    string StatePath { get; }
}
