using Mele.Engine.Platform;

namespace Mele.Tests;

public class PlatformTests
{
    private const string UserReg = """
        WINE REGISTRY Version 2
        ;; All keys relative to \\User\\S-1-5-21

        [Software\\ME3Tweaks] 1793633372
        #time=1dd6adef53dbe2c
        "ExecutableLocation"="C:\\users\\matt\\Documents\\ME3TweaksModManager\\ME3TweaksModManager.exe"
        "LE1VanillaBackupLocation"="X:\\Games\\ME1 Backup"
        "LE2VanillaBackupLocation"="X:\\Games\\ME \"Quoted\" Backups"

        [Software\\ME3Tweaks\\Sub] 1793633372
        "LE3VanillaBackupLocation"="X:\\wrong section"

        [Software\\Other] 1
        "LE3VanillaBackupLocation"="X:\\also wrong"
        """;

    /// <summary>A prefix with x: -> home, z: -> "/", like the real Proton prefix.</summary>
    private static (TempDir root, WinePrefix prefix, string home) MakePrefix()
    {
        var root = new TempDir();
        var pfx = Path.Combine(root.Path, "pfx");
        var home = Path.Combine(root.Path, "home");
        Directory.CreateDirectory(Path.Combine(pfx, "dosdevices"));
        Directory.CreateDirectory(Path.Combine(pfx, "drive_c"));
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(pfx, "user.reg"), UserReg.Replace("\r\n", "\n"));
        File.CreateSymbolicLink(Path.Combine(pfx, "dosdevices", "c:"), "../drive_c");
        File.CreateSymbolicLink(Path.Combine(pfx, "dosdevices", "x:"), home);
        File.CreateSymbolicLink(Path.Combine(pfx, "dosdevices", "z:"), "/");
        return (root, new WinePrefix(pfx), home);
    }

    [Fact]
    public void Reads_registry_values_from_exactly_the_right_section()
    {
        if (OperatingSystem.IsWindows()) return; // symlink fixture is Unix-only
        var (root, p, _) = MakePrefix();
        using var _r = root;
        Assert.Equal(@"X:\Games\ME1 Backup", p.ReadUserString(@"Software\ME3Tweaks", "LE1VanillaBackupLocation"));
        Assert.Equal("X:\\Games\\ME \"Quoted\" Backups", p.ReadUserString(@"Software\ME3Tweaks", "LE2VanillaBackupLocation"));
        Assert.Null(p.ReadUserString(@"Software\ME3Tweaks", "LE3VanillaBackupLocation")); // only exists in look-alike sections
        Assert.Equal(@"X:\wrong section", p.ReadUserString(@"Software\ME3Tweaks\Sub", "LE3VanillaBackupLocation"));
        Assert.Equal(@"X:\Games\ME1 Backup", p.ReadUserString(@"software\me3tweaks", "le1vanillabackuplocation")); // case-insensitive
    }

    [Fact]
    public void Maps_drive_letters_through_dosdevices()
    {
        if (OperatingSystem.IsWindows()) return;
        var (root, p, home) = MakePrefix();
        using var _r = root;
        Assert.Equal(Path.Combine(home, "Games", "ME1 Backup"), p.ToLinuxPath(@"X:\Games\ME1 Backup"));
        Assert.Equal(Path.Combine(p.Root, "drive_c", "users", "matt"), p.ToLinuxPath(@"C:\users\matt"));
        Assert.Null(p.ToLinuxPath(@"Q:\nowhere")); // unmapped drive
        Assert.Null(p.ToLinuxPath("not a windows path"));
        Assert.Equal(@"Z:\home\matt\a b\f.mem", p.ToWindowsPath("/home/matt/a b/f.mem"));
    }

    [Fact]
    public void Prefix_can_be_found_from_the_steam_compatdata_folder()
    {
        if (OperatingSystem.IsWindows()) return;
        var (root, p, _) = MakePrefix();
        using var _r = root;
        Assert.Equal(p.Root, WinePrefix.Find(root.Path)!.Root);   // parent of "pfx"
        Assert.Equal(p.Root, WinePrefix.Find(p.Root)!.Root);      // the prefix itself
        Assert.Null(WinePrefix.Find(Path.Combine(root.Path, "home")));
    }

    [Fact]
    public void Backup_rules_match_what_m3_accepts()
    {
        using var tmp = new TempDir();
        Assert.False(BackupRules.Validate("LE1", null).Valid);
        Assert.Contains("does not exist", BackupRules.Validate("LE1", Path.Combine(tmp.Path, "nope")).Reason);

        Directory.CreateDirectory(Path.Combine(tmp.Path, "Binaries"));
        Assert.Contains("BioGame", BackupRules.Validate("LE1", tmp.Path).Reason);

        Directory.CreateDirectory(Path.Combine(tmp.Path, "bioGAME")); // case-insensitive folder match
        Assert.Contains("cmm_vanilla", BackupRules.Validate("LE1", tmp.Path).Reason);

        File.WriteAllText(Path.Combine(tmp.Path, "cmm_vanilla"), "");
        Assert.True(BackupRules.Validate("LE1", tmp.Path).Valid);
    }

    [Fact]
    public void Wine_backup_locator_resolves_and_validates_each_game()
    {
        if (OperatingSystem.IsWindows()) return;
        var (root, p, home) = MakePrefix();
        using var _r = root;
        var good = Path.Combine(home, "Games", "ME1 Backup");
        Directory.CreateDirectory(Path.Combine(good, "BioGame"));
        Directory.CreateDirectory(Path.Combine(good, "Binaries"));
        File.WriteAllText(Path.Combine(good, "cmm_vanilla"), "");

        var loc = new WinePrefixBackupLocator(p);
        var le1 = loc.Get("LE1");
        Assert.True(le1.Valid);
        Assert.Equal(good, le1.Path);
        Assert.False(loc.Get("LE2").Valid);       // recorded, folder missing
        Assert.Contains("No backup recorded", loc.Get("LE3").Reason);
    }
}
