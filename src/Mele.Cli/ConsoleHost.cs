using Mele.Engine.Orchestration;
using Mele.Engine.Platform;

namespace Mele.Cli;

internal sealed class ConsoleHost(bool autoConfirmTextures) : IRunHost
{
    public void Report(RunEvent e)
    {
        var tag = e.Kind switch { RunEventKind.Error => "ERROR", RunEventKind.Warning => "warn ", RunEventKind.StepStarted => "==>  ", _ => "     " };
        Console.WriteLine($"{tag} {(e.Game != null ? e.Game + " " : "")}[{e.Step}] {e.Message}");
    }

    public Task<bool> ConfirmM3BatchDoneAsync(IReadOnlyList<M3Batch> batches, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("Now install these in Mod Manager (Batch Mod Installer > load > Install). They are already in its BatchModQueues folder:");
        foreach (var b in batches) Console.WriteLine($"   {b.Game}: {b.Title}  ({b.FileName})");
        Console.WriteLine("Do the games one at a time, with Mod Manager's backups already made.");
        if (Console.IsInputRedirected) { Console.WriteLine("(no terminal to ask; pausing. Run again after installing.)"); return Task.FromResult(false); }
        Console.Write("Type 'done' when every group is installed, or just press Enter to pause: ");
        return Task.FromResult(string.Equals(Console.ReadLine()?.Trim(), "done", StringComparison.OrdinalIgnoreCase));
    }

    public Task<bool> ConfirmTextureInstallAsync(string game, BackupInfo backup, CancellationToken ct)
    {
        Console.WriteLine($"\nAbout to install the guide's textures into {game}. This rewrites game files and takes 15-40 minutes.");
        Console.WriteLine($"Backup in use: {backup.Path}");
        if (autoConfirmTextures) { Console.WriteLine("(--yes given: continuing)"); return Task.FromResult(true); }
        if (Console.IsInputRedirected) { Console.WriteLine("(no terminal to ask; skipping. Use --yes to allow this unattended.)"); return Task.FromResult(false); }
        Console.Write("Continue? [y/N] ");
        return Task.FromResult(Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes");
    }
}
