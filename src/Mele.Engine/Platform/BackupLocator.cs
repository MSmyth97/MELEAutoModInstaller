using System.Runtime.Versioning;

namespace Mele.Engine.Platform;

/// <param name="Valid">True only for a folder M3/ME3TweaksCore would accept as a vanilla backup.</param>
public sealed record BackupInfo(string Game, string? Path, bool Valid, string Reason);

/// <summary>Finds the vanilla backups made by ME3Tweaks Mod Manager.</summary>
public interface IBackupLocator
{
    BackupInfo Get(string game);
}

public static class BackupRules
{
    public const string MarkerFile = "cmm_vanilla";

    /// <summary>Same checks ME3TweaksCore's BackupService applies: BioGame + Binaries folders and the cmm_vanilla marker.</summary>
    public static BackupInfo Validate(string game, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new(game, null, false, "No backup recorded by Mod Manager");
        if (!Directory.Exists(path)) return new(game, path, false, "Backup folder does not exist");
        bool Has(string name) => Directory.EnumerateDirectories(path).Any(d => string.Equals(System.IO.Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));
        if (!Has("BioGame") || !Has("Binaries")) return new(game, path, false, "Folder is missing BioGame/Binaries (not a game backup)");
        if (!File.Exists(System.IO.Path.Combine(path, MarkerFile))) return new(game, path, false, $"Not marked as vanilla ({MarkerFile} missing)");
        return new(game, path, true, "OK");
    }

    public static string ValueName(string game) => $"{game}VanillaBackupLocation";
    public const string RegistryKey = @"Software\ME3Tweaks";
}

/// <summary>Windows: M3 stores the paths in HKCU\Software\ME3Tweaks.</summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryBackupLocator : IBackupLocator
{
    public BackupInfo Get(string game)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(BackupRules.RegistryKey);
        return BackupRules.Validate(game, key?.GetValue(BackupRules.ValueName(game)) as string);
    }
}

/// <summary>Linux: reads the same values from the Wine prefix M3 runs in, and maps the drive letter to a Linux path.</summary>
public sealed class WinePrefixBackupLocator(WinePrefix prefix) : IBackupLocator
{
    public BackupInfo Get(string game)
    {
        var win = prefix.ReadUserString(BackupRules.RegistryKey, BackupRules.ValueName(game));
        if (win == null) return BackupRules.Validate(game, null);
        var linux = prefix.ToLinuxPath(win);
        return linux == null ? new(game, win, false, $"Drive of '{win}' is not mapped in the prefix") : BackupRules.Validate(game, linux);
    }
}
