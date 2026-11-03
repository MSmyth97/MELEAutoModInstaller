using Mele.Engine.Backup;
using Mele.Engine.Platform;

namespace Mele.Tests;

public class GameRestorerTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _backup, _game;
    public GameRestorerTests()
    {
        _backup = Directory.CreateDirectory(System.IO.Path.Combine(_tmp.Path, "ME1 Backup")).FullName;
        _game = Directory.CreateDirectory(System.IO.Path.Combine(_tmp.Path, "Game", "ME1")).FullName;
    }
    public void Dispose() => _tmp.Dispose();

    private static string P(string root, string rel) => System.IO.Path.Combine(root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static void Put(string root, string rel, string content = "x")
    {
        var path = P(root, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static BackupInfo Backup(string path) => new("LE1", path, true, "OK");

    /// <summary>An English-only backup, and a game that was modded after it: the situation we met on the first real run.</summary>
    private void ModdedGame()
    {
        foreach (var root in new[] { _backup, _game })
        {
            Put(root, "Binaries/Win64/MassEffect1.exe", "exe");
            Put(root, "BioGame/CookedPCConsole/Startup_INT.pcc", "intro");
        }
        Put(_backup, "cmm_vanilla", "");
        Put(_backup, "BioGame/CookedPCConsole/changed.pcc", "original");
        Put(_game, "BioGame/CookedPCConsole/changed.pcc", "modded and longer");
        Put(_backup, "BioGame/CookedPCConsole/gone.pcc", "vanilla");
        Put(_game, "BioGame/CookedPCConsole/Startup_DE.pcc", "german");                    // vanilla, but the backup has no German
        Put(_game, "BioGame/Content/Packages/ISACT/wrex_fr.isb", "audio");
        Put(_game, "BioGame/DLC/DLC_MOD_Vegas/CookedPCConsole/x.pcc", "mod");
        Put(_game, "BioGame/DLC/DLC_MOD_Vegas/AutoLoad.ini", "ini");
        Put(_game, "BioGame/CookedPCConsole/TexturesMEM0000.tfc", "tex");
        Put(_game, "Binaries/Win64/ASI/loader.asi", "asi");
        Put(_game, "BioGame/Config/GamerSettings.ini", "my graphics settings");           // the user's, kept across a restore
        Put(_game, "BioGame/CookedPCConsole/mystery.bin", "?");                           // recognised as nothing
    }

    private static GameRestorer Restorer(bool running = false) => new(() => running);

    [Fact]
    public async Task Restore_puts_the_game_back_keeps_vanilla_files_the_backup_lacks_and_leaves_the_rest_alone()
    {
        ModdedGame();
        var r = Restorer();
        var plan = r.Prepare("LE1", Backup(_backup), _game);
        var result = await r.RestoreAsync(plan);

        Assert.True(result.Success, string.Join("\n", result.Problems));
        Assert.Equal("original", File.ReadAllText(P(_game, "BioGame/CookedPCConsole/changed.pcc")));    // overwritten
        Assert.Equal("vanilla", File.ReadAllText(P(_game, "BioGame/CookedPCConsole/gone.pcc")));        // restored
        Assert.False(Directory.Exists(P(_game, "BioGame/DLC")));                                         // the mod folder and the emptied DLC folder are gone
        Assert.False(File.Exists(P(_game, "BioGame/CookedPCConsole/TexturesMEM0000.tfc")));
        Assert.False(File.Exists(P(_game, "Binaries/Win64/ASI/loader.asi")));
        Assert.True(File.Exists(P(_game, "BioGame/CookedPCConsole/Startup_DE.pcc")));                    // vanilla German survives
        Assert.True(File.Exists(P(_game, "BioGame/Content/Packages/ISACT/wrex_fr.isb")));
        Assert.Equal("my graphics settings", File.ReadAllText(P(_game, "BioGame/Config/GamerSettings.ini")));
        Assert.True(File.Exists(P(_game, "BioGame/CookedPCConsole/mystery.bin")));                       // unknown files are kept by default
        Assert.False(File.Exists(P(_backup, "BioGame/CookedPCConsole/TexturesMEM0000.tfc")));            // the backup is never changed
        Assert.Empty(Directory.EnumerateFiles(_game, "*.mele-restore", SearchOption.AllDirectories));
    }

    [Fact]
    public void Prepare_is_a_dry_run_that_changes_nothing_and_says_what_it_would_do()
    {
        ModdedGame();
        var before = Snapshot(_game);
        var plan = Restorer().Prepare("LE1", Backup(_backup), _game);

        Assert.Equal(before, Snapshot(_game));
        Assert.Equal(["BioGame/CookedPCConsole/changed.pcc", "BioGame/CookedPCConsole/gone.pcc"], plan.Copy.Order().ToArray());
        Assert.Equal(4, plan.Delete.Count);                                                              // two mod files, a texture file, the ASI
        Assert.DoesNotContain(plan.Delete, p => p.Contains("Startup_DE") || p.Contains("ISACT") || p.Contains("GamerSettings"));
        Assert.Equal(2, plan.KeptLocalised.Count);
        Assert.Equal(["BioGame/CookedPCConsole/mystery.bin"], plan.KeptUnknown);
        Assert.False(plan.NothingToDo);
        Assert.Equal(4, plan.Describe().Count());
    }

    [Fact]
    public async Task Unknown_files_are_deleted_only_when_asked()
    {
        ModdedGame();
        var r = Restorer();
        var plan = r.Prepare("LE1", Backup(_backup), _game, deleteUnknown: true);
        Assert.Contains("BioGame/CookedPCConsole/mystery.bin", plan.Delete);
        Assert.Empty(plan.KeptUnknown);
        await r.RestoreAsync(plan);
        Assert.False(File.Exists(P(_game, "BioGame/CookedPCConsole/mystery.bin")));
    }

    [Fact]
    public async Task A_second_restore_has_nothing_to_do()
    {
        ModdedGame();
        var r = Restorer();
        await r.RestoreAsync(r.Prepare("LE1", Backup(_backup), _game));
        Assert.True(r.Prepare("LE1", Backup(_backup), _game).NothingToDo);
    }

    [Fact]
    public async Task Restore_refuses_while_a_game_is_running_and_changes_nothing()
    {
        ModdedGame();
        var before = Snapshot(_game);
        var r = Restorer(running: true);
        var ex = await Assert.ThrowsAsync<RestoreException>(() => r.RestoreAsync(r.Prepare("LE1", Backup(_backup), _game)));
        Assert.Contains("running", ex.Message);
        Assert.Equal(before, Snapshot(_game));
    }

    [Fact]
    public void Prepare_refuses_unsafe_setups()
    {
        ModdedGame();
        var r = Restorer();
        Assert.Throws<RestoreException>(() => r.Prepare("LE1", new BackupInfo("LE1", null, false, "No backup recorded"), _game));
        var notAGame = Directory.CreateDirectory(System.IO.Path.Combine(_tmp.Path, "Documents")).FullName;
        Put(notAGame, "important.txt");
        Assert.Contains("does not look like a game folder", Assert.Throws<RestoreException>(() => r.Prepare("LE1", Backup(_backup), notAGame)).Message);
        Assert.Contains("inside each other", Assert.Throws<RestoreException>(() => r.Prepare("LE1", Backup(_game), _game)).Message);
        Assert.Contains("inside each other", Assert.Throws<RestoreException>(() => r.Prepare("LE1", Backup(System.IO.Path.Combine(_game, "BioGame")), _game)).Message);
        Assert.True(File.Exists(P(notAGame, "important.txt")));
    }

    [Fact]
    public async Task Cancelling_stops_cleanly_and_leaves_no_partial_files()
    {
        ModdedGame();
        var r = Restorer();
        var plan = r.Prepare("LE1", Backup(_backup), _game);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.RestoreAsync(plan, null, cts.Token));
        Assert.Empty(Directory.EnumerateFiles(_game, "*.mele-restore", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Progress_is_reported_for_each_phase()
    {
        ModdedGame();
        var phases = new List<string>();
        var r = Restorer();
        await r.RestoreAsync(r.Prepare("LE1", Backup(_backup), _game), new Progress<FileProgress>(p => { lock (phases) phases.Add(p.Phase); }));
        await Task.Delay(100);                                                                           // Progress<T> posts to the thread pool
        Assert.Contains("Removing mod files", phases);
        Assert.Contains("Restoring files", phases);
        Assert.Contains("Done", phases);
    }

    [Fact]
    public async Task Read_only_mod_files_are_deleted()
    {
        ModdedGame();
        var ro = P(_game, "BioGame/DLC/DLC_MOD_Vegas/AutoLoad.ini");
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        var r = Restorer();
        var result = await r.RestoreAsync(r.Prepare("LE1", Backup(_backup), _game));
        Assert.True(result.Success);
        Assert.False(File.Exists(ro));
    }

    [Fact]
    public async Task A_file_that_cannot_be_deleted_is_reported_and_the_restore_is_not_called_a_success()
    {
        if (OperatingSystem.IsWindows()) return;                                                         // relies on Unix folder permissions
        ModdedGame();
        var folder = P(_game, "BioGame/DLC/DLC_MOD_Vegas");
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);                  // cannot remove entries from it
        try
        {
            var r = Restorer();
            var result = await r.RestoreAsync(r.Prepare("LE1", Backup(_backup), _game));
            if (File.Exists(P(_game, "BioGame/DLC/DLC_MOD_Vegas/AutoLoad.ini")) is false) return;        // running as root: permissions do not apply here
            Assert.False(result.Success);
            Assert.Contains(result.Problems, m => m.Contains("Could not delete") && m.Contains("AutoLoad.ini"));
            Assert.Contains(result.Problems, m => m.Contains("could not be removed"));
        }
        finally { File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public async Task Copies_keep_the_original_timestamp()
    {
        ModdedGame();
        var stamp = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(P(_backup, "BioGame/CookedPCConsole/gone.pcc"), stamp);
        var r = Restorer();
        await r.RestoreAsync(r.Prepare("LE1", Backup(_backup), _game));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(P(_game, "BioGame/CookedPCConsole/gone.pcc")));
    }

    [Fact]
    public void Free_space_is_reported_for_a_real_folder()
    {
        var free = DiskSpace.FreeGb(_tmp.Path);
        Assert.True(free > 0);
        Assert.True(DiskSpace.FreeGb(System.IO.Path.Combine(_tmp.Path, "does", "not", "exist")) > 0);   // walks up to the nearest parent
    }

    private static string[] Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(f => $"{System.IO.Path.GetRelativePath(root, f)}|{new FileInfo(f).Length}").Order().ToArray();
}
