using Mele.Engine.Platform;
using Mele.Engine.Provision;

namespace Mele.Tests;

public class ModManagerTargetsTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _home, _game, _prefix;
    public ModManagerTargetsTests()
    {
        _home = Directory.CreateDirectory(System.IO.Path.Combine(_tmp.Path, "home", "matt")).FullName;
        _game = Directory.CreateDirectory(System.IO.Path.Combine(_home, "Games", "Mass Effect Legendary Edition", "Game", "ME1")).FullName;
        _prefix = Directory.CreateDirectory(System.IO.Path.Combine(_tmp.Path, "prefix")).FullName;
    }
    public void Dispose() => _tmp.Dispose();

    /// <summary>A prefix shaped like the app's own: Z: is the whole filesystem, X: is the user's home folder.</summary>
    private ModManagerTargets? Targets(params string[] lines)
    {
        if (OperatingSystem.IsWindows()) return null;
        File.WriteAllText(System.IO.Path.Combine(_prefix, "user.reg"), "WINE REGISTRY Version 2\n");
        var dev = Directory.CreateDirectory(System.IO.Path.Combine(_prefix, "dosdevices")).FullName;
        foreach (var (letter, target) in new[] { ("z:", "/"), ("x:", _home) })
            if (new FileInfo(System.IO.Path.Combine(dev, letter)).LinkTarget == null) File.CreateSymbolicLink(System.IO.Path.Combine(dev, letter), target);
        var data = Directory.CreateDirectory(System.IO.Path.Combine(_prefix, "drive_c", "ProgramData", "ME3TweaksModManager")).FullName;
        if (lines.Length > 0) File.WriteAllText(System.IO.Path.Combine(data, "GameTargetsLE1.txt"), string.Join("\r\n", lines) + "\r\n");   // CRLF, as Mod Manager writes it
        return ModManagerTargets.ForWinePrefix(WinePrefix.Find(_prefix)!);
    }

    private string Z(string linuxPath) => "Z:" + linuxPath.Replace('/', '\\');

    [Fact]
    public void The_folder_mod_manager_has_registered_matches()
    {
        var t = Targets(Z(_game));
        if (t == null) return;
        var c = t.Check("LE1", _game)!;
        Assert.True(c.Ok);
        Assert.Equal(TargetStatus.Matches, c.Status);
        Assert.Equal(Z(_game), c.PathToEnter);
    }

    [Fact]
    public void The_same_folder_through_another_drive_letter_also_matches()
    {
        var t = Targets(@"X:\Games\Mass Effect Legendary Edition\Game\ME1");                   // X: is the home folder in this prefix
        if (t == null) return;
        Assert.True(t.Check("LE1", _game)!.Ok);
    }

    [Fact]
    public void A_trailing_backslash_and_extra_blank_lines_do_not_matter()
    {
        var t = Targets("", Z(_game) + @"\", "   ");
        if (t == null) return;
        Assert.True(t.Check("LE1", _game)!.Ok);
    }

    [Fact]
    public void The_x_drive_mistake_is_found_and_the_right_path_is_shown()
    {
        var t = Targets(@"X:\home\matt\Games\Mass Effect Legendary Edition\Game\ME1");        // the path we typed by mistake: it does not exist
        if (t == null) return;
        var c = t.Check("LE1", _game)!;
        Assert.Equal(TargetStatus.Missing, c.Status);
        Assert.False(c.Ok);
        Assert.Contains("does not exist", c.Message);
        Assert.Contains(Z(_game), c.Message);
    }

    [Fact]
    public void A_drive_the_prefix_does_not_map_is_reported_as_such()
    {
        var t = Targets(@"Q:\Games\Mass Effect Legendary Edition\Game\ME1");
        if (t == null) return;
        var c = t.Check("LE1", _game)!;
        Assert.Equal(TargetStatus.Missing, c.Status);
        Assert.Contains("drive this prefix does not map", c.Message);
    }

    [Fact]
    public void A_target_that_exists_but_is_another_folder_is_a_different_install()
    {
        var other = Directory.CreateDirectory(System.IO.Path.Combine(_home, "Downloads", "repack", "Game", "ME1")).FullName;
        var t = Targets(Z(other));
        if (t == null) return;
        var c = t.Check("LE1", _game)!;
        Assert.Equal(TargetStatus.Different, c.Status);
        Assert.Contains("different folder", c.Message);
        Assert.Contains(Z(_game), c.Message);
    }

    [Fact]
    public void No_file_or_an_empty_file_means_no_target_yet()
    {
        var none = Targets();
        if (none == null) return;
        Assert.Equal(TargetStatus.NotRegistered, none.Check("LE1", _game)!.Status);
        File.WriteAllText(System.IO.Path.Combine(_prefix, "drive_c", "ProgramData", "ME3TweaksModManager", "GameTargetsLE1.txt"), "\r\n");
        Assert.Equal(TargetStatus.NotRegistered, Targets()!.Check("LE1", _game)!.Status);
        Assert.Contains("add the game", none.Check("LE1", _game)!.Message);
    }

    [Fact]
    public void Several_targets_are_fine_but_the_user_is_reminded_to_select_the_right_one()
    {
        var other = Directory.CreateDirectory(System.IO.Path.Combine(_home, "Downloads", "repack", "Game", "ME1")).FullName;
        var t = Targets(Z(other), Z(_game));
        if (t == null) return;
        var c = t.Check("LE1", _game)!;
        Assert.True(c.Ok);
        Assert.Contains("2 targets", c.Message);
    }

    [Fact]
    public void Each_game_has_its_own_list()
    {
        var t = Targets(Z(_game));
        if (t == null) return;
        var me2 = Directory.CreateDirectory(System.IO.Path.Combine(_home, "Games", "Mass Effect Legendary Edition", "Game", "ME2")).FullName;
        Assert.Equal(TargetStatus.NotRegistered, t.Check("LE2", me2)!.Status);                 // LE1's file says nothing about LE2
    }

    [Fact]
    public void Nothing_to_compare_when_mod_manager_is_not_set_up()
    {
        Assert.Null(new ModManagerTargets(null, _ => null, _ => null, false).Check("LE1", _game));
        Assert.Null(new ModManagerTargets(System.IO.Path.Combine(_tmp.Path, "no-such-folder"), _ => null, _ => null, false).Check("LE1", _game));
    }

    [Fact]
    public void On_windows_paths_compare_without_regard_to_case_and_are_shown_as_they_are()
    {
        var data = Directory.CreateDirectory(System.IO.Path.Combine(_tmp.Path, "ProgramData")).FullName;
        File.WriteAllText(System.IO.Path.Combine(data, "GameTargetsLE1.txt"), _game.ToUpperInvariant() + "\r\n");
        var t = new ModManagerTargets(data, p => p, p => p, caseInsensitive: true);
        var c = t.Check("LE1", _game)!;
        Assert.True(c.Ok);
        Assert.Equal(_game, c.PathToEnter);
        // The same text compared case-sensitively is a different folder that does not exist (on a case-sensitive file system).
        Assert.Equal(TargetStatus.Missing, new ModManagerTargets(data, p => p, p => p, caseInsensitive: false).Check("LE1", _game)!.Status);
    }
}
