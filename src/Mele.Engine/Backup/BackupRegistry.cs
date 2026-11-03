using System.Runtime.Versioning;
using Mele.Engine.Platform;
using Mele.Engine.Provision;

namespace Mele.Engine.Backup;

/// <summary>Tells Mod Manager where a game's vanilla backup is (the same value Mod Manager writes itself).</summary>
public interface IBackupRegistry
{
    /// <param name="backupPath">The backup folder as this process sees it. The implementation converts it to the form Mod Manager reads.</param>
    Task SetAsync(string game, string backupPath, CancellationToken ct = default);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsBackupRegistry : IBackupRegistry
{
    public Task SetAsync(string game, string backupPath, CancellationToken ct = default)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(BackupRules.RegistryKey);
        key.SetValue(BackupRules.ValueName(game), Path.GetFullPath(backupPath));
        return Task.CompletedTask;
    }
}

/// <summary>Linux: writes the value inside the Wine prefix, as a Windows path through the prefix's drive links, via Wine's reg tool.</summary>
public sealed class WineBackupRegistry(ProtonPrefix prefix) : IBackupRegistry
{
    public async Task SetAsync(string game, string backupPath, CancellationToken ct = default)
    {
        var windows = prefix.WindowsPathFor(Path.GetFullPath(backupPath))
            ?? throw new ProvisionException($"The prefix has no drive that maps to {backupPath}, so Mod Manager could not open it.");
        await prefix.SetRegistryStringAsync(BackupRules.RegistryKey, BackupRules.ValueName(game), windows, ct: ct);
        var stored = await prefix.GetRegistryStringAsync(BackupRules.RegistryKey, BackupRules.ValueName(game), ct);
        if (stored != windows) throw new ProvisionException($"Wine did not keep the backup path (wrote {windows}, read back {stored ?? "nothing"}).");
    }
}
