using Mele.Engine.Backup;
using Mele.Engine.Mem;
using Mele.Engine.Platform;
using Mele.Engine.Provision;

namespace Mele.Tests;

internal sealed class RecordingRegistry(Exception? fail = null) : IBackupRegistry
{
    public List<(string Game, string Path)> Set { get; } = [];
    public Task SetAsync(string game, string backupPath, CancellationToken ct = default)
    {
        if (fail != null) throw fail;
        Set.Add((game, backupPath));
        return Task.CompletedTask;
    }
}

/// <summary>Stands in for umu-run + Wine's reg tool: keeps a tiny registry, answers "reg query" in reg.exe's output format.</summary>
internal sealed class FakeRegRunner(int addExit = 0) : IProcessRunner
{
    public Dictionary<string, string> Values { get; } = [];
    public List<(string Exe, string[] Args, IReadOnlyDictionary<string, string>? Env)> Calls { get; } = [];
    public Task<int> RunAsync(string exe, IReadOnlyList<string> args, Action<string>? onLine = null, IReadOnlyDictionary<string, string>? env = null, CancellationToken ct = default)
    {
        Calls.Add((exe, args.ToArray(), env));
        if (args[1] == "add") { if (addExit == 0) Values[args[4]] = args[8]; return Task.FromResult(addExit); }
        if (Values.TryGetValue(args[4], out var v))
        {
            onLine?.Invoke(@"HKEY_CURRENT_USER\Software\ME3Tweaks");
            onLine?.Invoke($"    {args[4]}    REG_SZ    {v}");
            return Task.FromResult(0);
        }
        return Task.FromResult(1);
    }
}

public class BackupCreatorTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _gameDir, _game, _dest;
    public BackupCreatorTests()
    {
        _gameDir = Directory.CreateDirectory(System.IO.Path.Combine(_tmp.Path, "Games", "Mass Effect Legendary Edition")).FullName;
        _game = GameFolders.Root(_gameDir, "LE1");
        _dest = System.IO.Path.Combine(_tmp.Path, "Games", "ME1 Backup");
    }
    public void Dispose() => _tmp.Dispose();

    private static string P(string root, string rel) => System.IO.Path.Combine(root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
    private static void Put(string root, string rel, string content = "x")
    {
        var path = P(root, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void VanillaGame()
    {
        Put(_game, "Binaries/Win64/MassEffect1.exe", "exe");
        Put(_game, "BioGame/CookedPCConsole/Startup_INT.pcc", "intro");
        Put(_game, "BioGame/CookedPCConsole/Startup_DE.pcc", "german");
        Put(_game, "BioGame/Content/Packages/ISACT/wrex_fr.isb", "audio");
        Directory.CreateDirectory(P(_game, "BioGame/Config"));                                  // an empty folder is part of the game too
    }

    private static readonly BackupInfo None = new("LE1", null, false, "No backup recorded");
    private static BackupCreator Creator(IBackupRegistry reg, bool running = false) => new(reg, () => running);

    [Fact]
    public async Task Creates_a_verified_backup_with_the_marker_and_tells_mod_manager()
    {
        VanillaGame();
        var reg = new RecordingRegistry();
        var c = Creator(reg);
        var plan = c.Prepare("LE1", _game, _dest, None);
        Assert.Equal((4, 19L), (plan.Files, plan.Bytes));                                      // exe 3 + intro 5 + german 6 + audio 5 bytes
        var r = await c.CreateAsync(plan);

        Assert.True(r.Success && r.Registered, string.Join("\n", r.Problems));
        Assert.Equal([("LE1", plan.Destination)], reg.Set);
        Assert.True(BackupRules.Validate("LE1", _dest).Valid);                                  // what Mod Manager (and our locator) accept
        Assert.Equal("german", File.ReadAllText(P(_dest, "BioGame/CookedPCConsole/Startup_DE.pcc")));   // all languages kept
        Assert.True(Directory.Exists(P(_dest, "BioGame/Config")));
        Assert.Equal(0, new FileInfo(P(_dest, "cmm_vanilla")).Length);                          // the marker is an empty file
        Assert.True(BackupCompare.Compare(_dest, _game).GameContainsBackup);
    }

    [Fact]
    public void Prepare_changes_nothing_and_default_destination_is_next_to_the_game_folder()
    {
        VanillaGame();
        var plan = Creator(new RecordingRegistry()).Prepare("LE1", _game, _dest, None);
        Assert.False(Directory.Exists(_dest));
        Assert.Equal(_dest, BackupCreator.DefaultDestination(_gameDir, "LE1"));
        Assert.Equal(System.IO.Path.Combine(_tmp.Path, "Games", "ME3 Backup"), BackupCreator.DefaultDestination(_gameDir + "/", "LE3"));
        Assert.Equal(4, plan.Files);
    }

    [Fact]
    public void A_suggested_folder_is_never_one_that_already_holds_something()
    {
        var free = BackupCreator.SuggestDestination(_gameDir, "LE1");
        Assert.Equal(_dest, free);                                                           // nothing there yet: the plain name

        Put(_dest, "cmm_vanilla", "");                                                       // the existing backup
        var second = BackupCreator.SuggestDestination(_gameDir, "LE1");
        Assert.Equal(_dest + " 2", second);

        Put(second, "x");
        Assert.Equal(_dest + " 3", BackupCreator.SuggestDestination(_gameDir, "LE1"));

        Directory.CreateDirectory(_dest + " 3");                                             // an empty folder is fine to use
        Assert.Equal(_dest + " 3", BackupCreator.SuggestDestination(_gameDir, "LE1"));
        VanillaGame();
        Assert.NotNull(Creator(new RecordingRegistry()).Prepare("LE1", _game, BackupCreator.SuggestDestination(_gameDir, "LE1"), new BackupInfo("LE1", _dest, true, "OK"), replaceExisting: true));
    }

    [Fact]
    public void Refuses_to_back_up_a_modded_game()
    {
        VanillaGame();
        Put(_game, "BioGame/DLC/DLC_MOD_Vegas/x.pcc");
        var ex = Assert.Throws<BackupException>(() => Creator(new RecordingRegistry()).Prepare("LE1", _game, _dest, None));
        Assert.Contains("not vanilla", ex.Message);
        Assert.Contains("DLC_MOD_Vegas", ex.Message);
        Assert.False(Directory.Exists(_dest));
    }

    [Fact]
    public void Refuses_unsafe_destinations_and_non_game_folders_and_an_existing_backup()
    {
        VanillaGame();
        var c = Creator(new RecordingRegistry());
        Put(_dest, "something.txt");
        Assert.Contains("not empty", Assert.Throws<BackupException>(() => c.Prepare("LE1", _game, _dest, None)).Message);
        Assert.Contains("separate places", Assert.Throws<BackupException>(() => c.Prepare("LE1", _game, P(_game, "Backup"), None)).Message);
        Assert.Contains("separate places", Assert.Throws<BackupException>(() => c.Prepare("LE1", _game, _gameDir, None)).Message);
        var docs = Directory.CreateDirectory(System.IO.Path.Combine(_tmp.Path, "Documents")).FullName;
        Assert.Contains("does not look like a game folder", Assert.Throws<BackupException>(() => c.Prepare("LE1", docs, P(_tmp.Path, "Elsewhere"), None)).Message);

        var existing = new BackupInfo("LE1", "/somewhere/ME1 Backup", true, "OK");
        File.Delete(P(_dest, "something.txt"));
        Assert.Contains("already has a backup", Assert.Throws<BackupException>(() => c.Prepare("LE1", _game, _dest, existing)).Message);
        Assert.NotNull(c.Prepare("LE1", _game, _dest, existing, replaceExisting: true));        // replacing is an explicit choice
    }

    [Fact]
    public async Task Refuses_while_a_game_is_running()
    {
        VanillaGame();
        var c = Creator(new RecordingRegistry(), running: true);
        var plan = c.Prepare("LE1", _game, _dest, None);
        await Assert.ThrowsAsync<BackupException>(() => c.CreateAsync(plan));
        Assert.False(Directory.Exists(_dest));
    }

    [Fact]
    public async Task A_cancelled_copy_never_leaves_something_that_looks_like_a_valid_backup()
    {
        VanillaGame();
        var c = Creator(new RecordingRegistry());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.CreateAsync(c.Prepare("LE1", _game, _dest, None), null, cts.Token));
        Assert.False(File.Exists(P(_dest, "cmm_vanilla")));
        Assert.False(BackupRules.Validate("LE1", _dest).Valid);
    }

    [Fact]
    public async Task A_file_that_cannot_be_copied_means_no_marker_and_no_registration()
    {
        if (OperatingSystem.IsWindows()) return;                                                // relies on Unix file permissions
        VanillaGame();
        var locked = P(_game, "BioGame/CookedPCConsole/Startup_DE.pcc");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            if (new FileInfo(locked).Length > 0 && CanRead(locked)) return;                      // running as root: permissions do not apply
            var reg = new RecordingRegistry();
            var c = Creator(reg);
            var r = await c.CreateAsync(c.Prepare("LE1", _game, _dest, None));
            Assert.False(r.Success);
            Assert.Contains(r.Problems, m => m.Contains("Could not copy") && m.Contains("Startup_DE.pcc"));
            Assert.False(File.Exists(P(_dest, "cmm_vanilla")));
            Assert.Empty(reg.Set);
        }
        finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite); }

        static bool CanRead(string p) { try { File.OpenRead(p).Dispose(); return true; } catch (UnauthorizedAccessException) { return false; } }
    }

    [Fact]
    public async Task If_mod_manager_cannot_be_told_the_backup_is_still_made_and_the_user_is_told_how_to_link_it()
    {
        VanillaGame();
        var c = Creator(new RecordingRegistry(new ProvisionException("reg exit 1")));
        var r = await c.CreateAsync(c.Prepare("LE1", _game, _dest, None));
        Assert.True(r.Success);
        Assert.False(r.Registered);
        Assert.Contains(r.Problems, m => m.Contains("link an existing backup") && m.Contains(_dest));
        Assert.True(BackupRules.Validate("LE1", _dest).Valid);
    }

    [Fact]
    public async Task Round_trip_backup_then_mod_then_restore_gives_back_the_original_game()
    {
        VanillaGame();
        var c = Creator(new RecordingRegistry());
        await c.CreateAsync(c.Prepare("LE1", _game, _dest, None));

        Put(_game, "BioGame/CookedPCConsole/Startup_INT.pcc", "modded and much longer");
        Put(_game, "BioGame/DLC/DLC_MOD_Vegas/x.pcc");
        Put(_game, "BioGame/CookedPCConsole/TexturesMEM0000.tfc");
        File.Delete(P(_game, "Binaries/Win64/MassEffect1.exe"));

        var restorer = new GameRestorer(() => false);
        var result = await restorer.RestoreAsync(restorer.Prepare("LE1", BackupRules.Validate("LE1", _dest), _game));
        Assert.True(result.Success, string.Join("\n", result.Problems));
        Assert.Equal("intro", File.ReadAllText(P(_game, "BioGame/CookedPCConsole/Startup_INT.pcc")));
        Assert.Equal("exe", File.ReadAllText(P(_game, "Binaries/Win64/MassEffect1.exe")));
        Assert.False(Directory.Exists(P(_game, "BioGame/DLC")));
        Assert.True(VanillaCheck.Check("LE1", _game).IsVanilla);
        Assert.True(BackupCompare.Compare(_dest, _game).GameContainsBackup);
    }

    // --- Wine registry plumbing -------------------------------------------------------------------------------------------------

    private ProtonPrefix MakePrefix(FakeRegRunner runner)
    {
        var root = Directory.CreateDirectory(System.IO.Path.Combine(_tmp.Path, "prefix")).FullName;
        File.WriteAllText(System.IO.Path.Combine(root, "user.reg"), "WINE REGISTRY Version 2\n");
        Directory.CreateDirectory(System.IO.Path.Combine(root, "dosdevices"));
        File.CreateSymbolicLink(System.IO.Path.Combine(root, "dosdevices", "z:"), "/");
        return new ProtonPrefix(root, runner, () => "/usr/bin/umu-run");
    }

    [Fact]
    public async Task Registering_runs_wines_reg_tool_in_the_prefix_with_a_windows_path_and_reads_it_back()
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new FakeRegRunner();
        var prefix = MakePrefix(runner);
        await new WineBackupRegistry(prefix).SetAsync("LE1", _dest);

        var add = runner.Calls[0];
        Assert.Equal("/usr/bin/umu-run", add.Exe);
        Assert.Equal(["reg", "add", @"HKCU\Software\ME3Tweaks", "/v", "LE1VanillaBackupLocation", "/t", "REG_SZ", "/d", @"Z:" + _dest.Replace('/', '\\'), "/f"], add.Args);
        Assert.Equal("runinprefix", add.Env!["PROTON_VERB"]);                                   // must not wait for Mod Manager's running Wine server
        Assert.Equal(prefix.Root, add.Env["WINEPREFIX"]);
        Assert.Equal("query", runner.Calls[1].Args[1]);                                         // verified through Wine, not by reading user.reg
    }

    [Fact]
    public async Task A_failed_reg_call_or_a_value_that_does_not_stick_is_an_error()
    {
        if (OperatingSystem.IsWindows()) return;
        await Assert.ThrowsAsync<ProvisionException>(() => new WineBackupRegistry(MakePrefix(new FakeRegRunner(addExit: 1))).SetAsync("LE1", _dest));
    }

    [Fact]
    public async Task Registering_fails_clearly_when_the_prefix_cannot_see_the_folder()
    {
        var root = Directory.CreateDirectory(System.IO.Path.Combine(_tmp.Path, "prefix2")).FullName;
        File.WriteAllText(System.IO.Path.Combine(root, "user.reg"), "x\n");                      // no drive links at all
        var prefix = new ProtonPrefix(root, new FakeRegRunner(), () => "/usr/bin/umu-run");
        var ex = await Assert.ThrowsAsync<ProvisionException>(() => new WineBackupRegistry(prefix).SetAsync("LE1", _dest));
        Assert.Contains("no drive", ex.Message);
    }
}
