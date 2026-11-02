using Mele.Engine.Platform;

namespace Mele.Engine.Orchestration;

public enum RunEventKind { Info, StepStarted, StepFinished, Warning, Error }
public sealed record RunEvent(RunEventKind Kind, string Step, string? Game, string Message);

/// <summary>A group the user must load in Mod Manager's Batch Mod Installer.</summary>
public sealed record M3Batch(string Game, string Title, string FileName);

/// <summary>Everything the orchestrator needs from a UI: progress, and the two points where a person must decide.</summary>
public interface IRunHost
{
    void Report(RunEvent e);

    /// <summary>The user installs the listed groups in M3. Return true once they say they are done; false to pause the run (it resumes later).</summary>
    Task<bool> ConfirmM3BatchDoneAsync(IReadOnlyList<M3Batch> batches, CancellationToken ct);

    /// <summary>The one destructive step: MEM rewrites game files. Return false to skip it.</summary>
    Task<bool> ConfirmTextureInstallAsync(string game, BackupInfo backup, CancellationToken ct);
}
