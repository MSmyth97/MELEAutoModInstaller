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
