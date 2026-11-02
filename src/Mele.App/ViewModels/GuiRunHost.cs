using Mele.Engine.Orchestration;
using Mele.Engine.Platform;

namespace Mele.App.ViewModels;

/// <summary>Bridges the orchestrator's host callbacks to the window: log lines, and the two questions that wait for a click.</summary>
public sealed class GuiRunHost(Action<RunEvent> onEvent, Func<IReadOnlyList<M3Batch>, CancellationToken, Task<bool>> askHandoff,
    Func<string, BackupInfo, CancellationToken, Task<bool>> askTextures) : IRunHost
{
    public void Report(RunEvent e) => onEvent(e);
    public Task<bool> ConfirmM3BatchDoneAsync(IReadOnlyList<M3Batch> batches, CancellationToken ct) => askHandoff(batches, ct);
    public Task<bool> ConfirmTextureInstallAsync(string game, BackupInfo backup, CancellationToken ct) => askTextures(game, backup, ct);
}
