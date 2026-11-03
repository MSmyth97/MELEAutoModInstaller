using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Mele.App.ViewModels;
using Mele.App.Views;
using Mele.Engine.Orchestration;
using Mele.Engine.Platform;

namespace Mele.App.Tests;

/// <summary>Renders each page to a PNG (folder from MELE_SHOTS, default: temp) so the layout can be inspected, and checks it lays out without exceptions.</summary>
public class RenderTests : IDisposable
{
    private readonly ViewModelTests _helper = new();
    public void Dispose() => _helper.Dispose();

    private static string OutDir
    {
        get
        {
            var d = Environment.GetEnvironmentVariable("MELE_SHOTS") ?? Path.Combine(Path.GetTempPath(), "mele-shots");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static void Shot(MainViewModel vm, string name, ThemeVariant? theme = null, int w = 920, int h = 760)
    {
        Application.Current!.RequestedThemeVariant = theme ?? ThemeVariant.Light;
        var win = new MainWindow { DataContext = vm, Width = w, Height = h };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = win.CaptureRenderedFrame() ?? throw new InvalidOperationException("nothing rendered");
        frame.Save(Path.Combine(OutDir, name + ".png"));
        win.Close();
    }

    private MainViewModel Ready()
    {
        var vm = _helper.Vm(new FakeBackend { KeySaved = true });
        vm.KeyOk = true;
        vm.KeyStatus = "Connected as Tester (Premium). Key saved.";
        return vm;
    }

    [AvaloniaFact] public void Setup_first_launch()
    {
        var vm = new MainViewModel(new FakeBackend { M3 = new(false, "/x", false, true, false) }, new AppSettings(), settingsFile: Path.Combine(Path.GetTempPath(), "none.json"));
        Shot(vm, "01_setup_first_launch", h: 1500);
    }

    [AvaloniaFact] public void Setup_ready_to_go() => Shot(Ready(), "02_setup_ready", h: 1500);

    [AvaloniaFact] public void Setup_backups_with_a_question_and_results()
    {
        var b = new FakeBackend { KeySaved = true, BackupList = [new("LE1", null, false, "No backup recorded by Mod Manager"), new("LE2", "/home/u/Games/ME2 Backup", true, "OK"), new("LE3", "/home/u/Games/ME3 Backup", true, "OK")] };
        var vm = _helper.Vm(b);
        vm.ConfirmTitle = "Restore LE2 from its backup?";
        foreach (var l in new[] { "Copy 2714 file(s) from the backup (6.9 GB): 0 missing, 2714 different", "Delete 3619 file(s) that mods or tools added",
                     "Keep 3161 vanilla file(s) in languages the backup does not contain",
                     "Your graphics settings (GamerSettings.ini) are kept. Close the game first. The backup itself is not changed." }) vm.ConfirmLines.Add(l);
        vm.BackupLines.Add("LE1: the game is not vanilla (53 mod folder(s) in BioGame/DLC (for example DLC_MOD_ALOT, DLC_MOD_ALOVLE1); 32 texture file(s) written by Mass Effect Modder or texture mods). A backup of a modded game would bring the mods back when restored. Restore the game from a real backup first.");
        Shot(vm, "09_backups_question", h: 1700);
    }

    [AvaloniaFact] public void Setup_several_installs_found()
    {
        var vm = new MainViewModel(new FakeBackend { KeySaved = true }, new AppSettings(), settingsFile: Path.Combine(Path.GetTempPath(), "none.json"));
        vm.DetectStatus = "Found 2 installs. Choose the one to use:";
        vm.Detected.Add(new DetectedRow("/home/u/.local/share/Steam/steamapps/common/Mass Effect Legendary Edition", "3 game(s): LE1, LE2, LE3   (found in Steam)", new CommunityToolkit.Mvvm.Input.RelayCommand(() => { })));
        vm.Detected.Add(new DetectedRow("/home/u/Games/Mass Effect Legendary Edition", "1 game(s): LE1   (found in a folder scan)", new CommunityToolkit.Mvvm.Input.RelayCommand(() => { })));
        Shot(vm, "13_find_game_several", h: 900);
    }

    [AvaloniaFact] public void Setup_mod_manager_targets()
    {
        var b = new FakeBackend { KeySaved = true, TargetList = [
            new("LE1", Mele.Engine.Provision.TargetStatus.Matches, "Mod Manager's target for LE1 is this game folder.", @"Z:\home\u\Games\Mass Effect Legendary Edition\Game\ME1", []),
            new("LE2", Mele.Engine.Provision.TargetStatus.Missing, @"Mod Manager's target for LE2 (X:\home\u\Games\Mass Effect Legendary Edition\Game\ME2) does not exist. Add the game again with this path: Z:\home\u\Games\Mass Effect Legendary Edition\Game\ME2", @"Z:\home\u\Games\Mass Effect Legendary Edition\Game\ME2", []),
            new("LE3", Mele.Engine.Provision.TargetStatus.NotRegistered, @"Mod Manager does not have LE3 as a target yet. In Mod Manager add the game, using this path: Z:\home\u\Games\Mass Effect Legendary Edition\Game\ME3", @"Z:\home\u\Games\Mass Effect Legendary Edition\Game\ME3", [])] };
        Shot(_helper.Vm(b), "12_mod_manager_targets", h: 1500);
    }

    [AvaloniaFact] public void Setup_new_backup_asks_for_a_folder()
    {
        var b = new FakeBackend { KeySaved = true, BackupList = [new("LE1", "/home/u/Games/ME1 Backup", true, "OK"), new("LE2", null, false, "No backup recorded by Mod Manager"), new("LE3", null, false, "No backup recorded by Mod Manager")] };
        var vm = _helper.Vm(b);
        vm.ConfirmTitle = "Back up LE1?";
        foreach (var l in new[] { "Copy 3685 files (15.2 GB) from /home/u/Games/Mass Effect Legendary Edition/Game/ME1",
                     "then tell Mod Manager where the backup is. This takes a few minutes and does not change the game.",
                     "Mod Manager will use the new backup from now on. The old one (/home/u/Games/ME1 Backup) stays on disk; delete it yourself if you no longer want it." }) vm.ConfirmLines.Add(l);
        vm.ConfirmFolder = "/home/u/Games/ME1 Backup 2";
        vm.ConfirmFolderVisible = true;
        Shot(vm, "11_new_backup_folder", h: 1700);
    }

    [AvaloniaFact] public void Setup_backups_while_working_in_dark_mode()
    {
        var b = new FakeBackend { KeySaved = true };
        var vm = _helper.Vm(b);
        vm.BackupLines.Add("LE1: restored 2714 file(s) and removed 3619. The game now matches its backup.");
        vm.BackupLines.Add("LE2: matches its backup and looks vanilla");
        vm.BackupLines.Add("  Vanilla check: no mods or texture installs found");
        vm.IsBusy = true; vm.BusyText = "Restoring files: 1300 of 2714";
        Shot(vm, "10_backups_busy_dark", ThemeVariant.Dark, h: 1700);
    }

    [AvaloniaFact] public void Setup_with_problems()
    {
        var b = new FakeBackend
        {
            KeySaved = true,
            BackupList = [new("LE1", "/b1", true, "OK"), new("LE2", null, false, "No backup recorded by Mod Manager"), new("LE3", "/b3", false, "Not marked as vanilla (cmm_vanilla missing)")],
            Plan = new RunPlan([new PreflightIssue(true, "LE2: no valid Mod Manager backup (No backup recorded by Mod Manager). Make one in Mod Manager before modding.")], []),
        };
        var vm = _helper.Vm(b);
        vm.CheckCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Shot(vm, "03_setup_problems", h: 1500);
    }

    [AvaloniaFact] public void Setup_dark_theme() => Shot(Ready(), "04_setup_dark", ThemeVariant.Dark, h: 1500);

    [AvaloniaFact] public void Run_handoff()
    {
        var vm = Ready();
        vm.Page = Page.Run; vm.IsBusy = true; vm.CurrentStep = "Your turn: install in Mod Manager";
        foreach (var (t, m, k) in new[] { ("08:41:02", "LE1  Downloading and staging 63 mods", RunEventKind.StepStarted), ("08:41:09", "LE1  Community Patch: Installed (downloaded)", RunEventKind.Info),
            ("08:44:50", "LE1  ALOT for LE1: Staged (downloaded; Ok)", RunEventKind.Info), ("08:58:12", "LE2  A mod could not be downloaded: HTTP 503", RunEventKind.Error), ("08:58:30", "LE2  The guide's mod list changed since you last installed it", RunEventKind.Warning) })
            vm.Log.Add(new LogLine(t, m, k == RunEventKind.Error, k == RunEventKind.Warning, k == RunEventKind.StepStarted));
        vm.Handoff = [new M3BatchRow("LE1: LE1 mods (guide)  (LE1 Mod Collection.biq2)"), new M3BatchRow("LE1: LE1 ALOT + ISL  (LE1 Textures M3TO (generated).biq2)")];
        Shot(vm, "05_run_handoff");
    }

    [AvaloniaFact] public void Run_texture_question()
    {
        var vm = Ready();
        vm.Page = Page.Run; vm.IsBusy = true; vm.CurrentStep = "LE1: Installing textures";
        vm.Log.Add(new LogLine("09:10:00", "LE1  Textures: ready", false, false, false));
        vm.TextureQuestion = "Install the guide's textures into LE1? This rewrites game files and takes 15 to 40 minutes. Your backup: /home/matt/Games/ME1 Backup";
        Shot(vm, "06_run_texture_question");
    }

    [AvaloniaFact] public void Done_success()
    {
        var vm = Ready();
        vm.ResultTitle = "All done"; vm.ResultDetail = "Mods and textures are installed. A few steps can only be done by hand (below)."; vm.ResultOk = true;
        vm.Notes.Add("LE2: 'A Lot of Videos for LE2' has no download link and must be obtained manually.");
        vm.ManualSteps.Add(new ChecklistItem("LE1: Apply the Shepard hair mods you want with Trilogy Save Editor."));
        vm.ManualSteps.Add(new ChecklistItem("LE3: In Mod Manager: Tools > EGM Settings > Mission Timings > Citadel DLC: choose 'Disable DLC'."));
        vm.Page = Page.Done;
        Shot(vm, "07_done");
    }

    [AvaloniaFact] public void Narrow_window_still_lays_out() => Shot(Ready(), "08_setup_narrow", w: 720, h: 640);
}
