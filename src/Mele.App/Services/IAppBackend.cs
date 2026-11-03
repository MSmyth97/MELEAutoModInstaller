using Mele.Engine.Backup;
using Mele.Engine.Orchestration;
using Mele.Engine.Platform;
using Mele.Engine.Provision;

namespace Mele.App.Services;

public sealed record M3Status(bool Installed, string Path, bool PrefixReady, bool NeedsPrefix, bool UmuMissing);

/// <summary>Everything the window needs from the machine and the engine. The real one talks to the network; tests use a fake.</summary>
public interface IAppBackend
{
    Task<(bool Ok, string Message)> CheckKeyAsync(string key, CancellationToken ct);
    bool HasSavedKey { get; }
    IReadOnlyList<BackupInfo> GetBackups(AppSettings s);
    M3Status GetM3Status(AppSettings s);
    /// <summary>Does Mod Manager have each selected game's folder as a target? Empty until Mod Manager has been set up.</summary>
    IReadOnlyList<TargetCheck> GetTargets(AppSettings s);
    Task SetupM3Async(AppSettings s, IProgress<string> progress, CancellationToken ct);
    void LaunchM3(AppSettings s);
    Task<RunPlan> PlanAsync(AppSettings s, IRunHost host, CancellationToken ct);
    Task<RunResult> RunAsync(AppSettings s, IRunHost host, CancellationToken ct);
    // Game backups. The Plan* calls change nothing and throw BackupException/RestoreException with a message fit to show the user.
    Task<GameReport> CheckGameAsync(AppSettings s, string game, CancellationToken ct);
    /// <param name="destination">Where to put the backup; null for a suggested folder next to the game.</param>
    /// <param name="replaceExisting">Make a new backup although Mod Manager already has one; the old one stays on disk.</param>
    Task<BackupPlan> PlanBackupAsync(AppSettings s, string game, string? destination, bool replaceExisting, CancellationToken ct);
    Task<BackupResult> CreateBackupAsync(AppSettings s, BackupPlan plan, IProgress<FileProgress> progress, CancellationToken ct);
    Task<RestorePlan> PlanRestoreAsync(AppSettings s, string game, CancellationToken ct);
    Task<RestoreResult> RestoreAsync(AppSettings s, RestorePlan plan, IProgress<FileProgress> progress, CancellationToken ct);
    void ResetState();
    string StatePath { get; }
}
