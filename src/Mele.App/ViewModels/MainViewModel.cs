using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mele.App.Services;
using Mele.Engine.Orchestration;
using Mele.Engine.Platform;

namespace Mele.App.ViewModels;

public enum Page { Setup, Run, Done }

public sealed record LogLine(string Time, string Text, bool IsError, bool IsWarning, bool IsHeading);
public sealed record BackupRow(string Game, bool Ok, string Text);
public sealed record ChecklistItem(string Text);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IAppBackend _backend;
    private readonly Action<Action> _ui;
    private readonly string? _settingsFile;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _decision;

    public MainViewModel(IAppBackend backend, AppSettings? settings = null, Action<Action>? ui = null, string? settingsFile = null)
    {
        _backend = backend;
        _ui = ui ?? (a => a());
        _settingsFile = settingsFile;
        var s = settings ?? AppSettings.Load(settingsFile);
        gameDir = s.GameDir; prefixDir = s.PrefixDir; le1 = s.Le1; le2 = s.Le2; le3 = s.Le3; includeTextures = s.IncludeTextures;
        keyStatus = backend.HasSavedKey ? "A key is saved. Enter a new one only to replace it." : "No key saved yet.";
        Refresh();
    }

    // ---- settings ---------------------------------------------------------------------------------------------
    [ObservableProperty] private string apiKeyInput = "";
    [ObservableProperty] private string keyStatus;
    [ObservableProperty] private bool keyOk;
    [ObservableProperty] private string gameDir;
    [ObservableProperty] private string prefixDir;
    [ObservableProperty] private bool le1, le2, le3, includeTextures;

    public bool IsLinux => !OperatingSystem.IsWindows();
    public AppSettings Settings => new() { GameDir = GameDir.Trim(), PrefixDir = PrefixDir.Trim(), Le1 = Le1, Le2 = Le2, Le3 = Le3, IncludeTextures = IncludeTextures };

    // ---- status -----------------------------------------------------------------------------------------------
    [ObservableProperty] private string m3Status = "";
    [ObservableProperty] private bool m3Ready;
    [ObservableProperty] private string gameDirStatus = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanStart))] private bool gameDirOk;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanStart))] private bool anyGameSelected;
    public ObservableCollection<BackupRow> Backups { get; } = [];

    // ---- navigation / run state -------------------------------------------------------------------------------
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OnSetup), nameof(OnRun), nameof(OnDone))] private Page page = Page.Setup;
    public bool OnSetup => Page == Page.Setup;
    public bool OnRun => Page == Page.Run;
    public bool OnDone => Page == Page.Done;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string busyText = "";
    [ObservableProperty] private string currentStep = "";
    public ObservableCollection<LogLine> Log { get; } = [];

    // Questions that wait for a click while a run is in progress.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HandoffPending))] private IReadOnlyList<M3BatchRow>? handoff;
    public bool HandoffPending => Handoff != null;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TextureQuestionPending))] private string? textureQuestion;
    public bool TextureQuestionPending => TextureQuestion != null;

    // Results.
    [ObservableProperty] private string resultTitle = "";
    [ObservableProperty] private string resultDetail = "";
    [ObservableProperty] private bool resultOk;
    public ObservableCollection<string> Notes { get; } = [];
    public ObservableCollection<ChecklistItem> ManualSteps { get; } = [];
    public ObservableCollection<string> PlanLines { get; } = [];

    public void Refresh()
    {
        var s = Settings;
        AnyGameSelected = s.Games.Count > 0;

        if (string.IsNullOrWhiteSpace(s.GameDir)) { GameDirOk = false; GameDirStatus = "Choose the 'Mass Effect Legendary Edition' folder."; }
        else
        {
            var missing = s.Games.Select(g => (g, dir: Path.Combine(s.GameDir, "Game", $"ME{g[2]}"))).Where(x => !Directory.Exists(x.dir)).Select(x => x.g).ToList();
            GameDirOk = Directory.Exists(s.GameDir) && missing.Count == 0 && AnyGameSelected;
            GameDirStatus = !Directory.Exists(s.GameDir) ? "That folder does not exist."
                : missing.Count > 0 ? $"Not a Legendary Edition folder (no game folder for {string.Join(", ", missing)})."
                : !AnyGameSelected ? "Pick at least one game." : "Looks right.";
        }

        var m3 = _backend.GetM3Status(s);
        M3Ready = m3.Installed;
        M3Status = m3.Installed ? $"Installed: {m3.Path}"
            : m3.UmuMissing ? "Not installed. This needs umu-launcher (umu-run) on Linux; install it with your package manager first."
            : "Not installed yet.";

        Backups.Clear();
        foreach (var b in _backend.GetBackups(s).Where(b => s.Games.Contains(b.Game)))
            Backups.Add(new BackupRow(b.Game, b.Valid, b.Valid ? $"{b.Path}" : b.Reason));
    }

    partial void OnGameDirChanged(string value) => Refresh();
    partial void OnPrefixDirChanged(string value) => Refresh();
    partial void OnLe1Changed(bool value) => Refresh();
    partial void OnLe2Changed(bool value) => Refresh();
    partial void OnLe3Changed(bool value) => Refresh();

    public bool CanStart => GameDirOk && AnyGameSelected;

    // ---- setup commands ---------------------------------------------------------------------------------------
    [RelayCommand]
    private async Task SaveKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiKeyInput)) { KeyOk = false; KeyStatus = "Paste your Nexus personal API key first."; return; }
        await Busy("Checking the key with Nexus", async ct =>
        {
            var (ok, msg) = await _backend.CheckKeyAsync(ApiKeyInput.Trim(), ct);
            KeyOk = ok;
            KeyStatus = msg;
            if (ok) ApiKeyInput = ""; // saved by the backend; do not keep it on screen
        });
    }

    [RelayCommand]
    private async Task SetupM3Async()
    {
        await Busy("Setting up Mod Manager", async ct =>
        {
            try { await _backend.SetupM3Async(Settings, new Progress<string>(m => _ui(() => BusyText = m)), ct); }
            catch (Exception e) when (e is not OperationCanceledException) { M3Status = $"Setup failed: {e.Message}"; return; }
            Refresh();
        });
    }

    [RelayCommand]
    private void LaunchM3()
    {
        try { _backend.LaunchM3(Settings); }
        catch (Exception e) { M3Status = $"Could not start Mod Manager: {e.Message}"; }
    }

    [RelayCommand] private void RefreshStatus() => Refresh();

    [RelayCommand]
    private async Task CheckAsync()
    {
        PlanLines.Clear();
        Settings.Save(_settingsFile);
        await Busy("Checking", async ct =>
        {
            try
            {
                var plan = await _backend.PlanAsync(Settings, new GuiRunHost(_ => { }, (_, _) => Task.FromResult(false), (_, _, _) => Task.FromResult(false)), ct);
                foreach (var i in plan.Issues) PlanLines.Add($"{(i.Blocking ? "Problem" : "Note")}: {i.Message}");
                if (plan.Issues.Any(i => i.Blocking)) PlanLines.Add("A run would stop at the problems above. Nothing has been changed.");
                else
                    foreach (var g in plan.Games)
                        PlanLines.Add($"{g.Game}: {g.ToInstall} mods to download, {g.Staged} already staged"
                            + (g.State.TexturesInstalled ? "; textures installed" : "") + (g.GuideChanged ? "; the guide changed since you last installed it" : ""));
            }
            catch (Exception e) when (e is not OperationCanceledException) { PlanLines.Add($"Problem: {e.Message}"); }
        });
    }

    // ---- the run ----------------------------------------------------------------------------------------------
    [RelayCommand]
    private async Task StartAsync()
    {
        Refresh(); // the folder may have changed on disk since the window last looked
        if (!CanStart) return;
        Settings.Save(_settingsFile);
        Log.Clear(); Notes.Clear(); ManualSteps.Clear();
        Page = Page.Run;
        IsBusy = true;
        BusyText = "Working. You can stop at any time.";
        _cts = new CancellationTokenSource();
        var host = new GuiRunHost(e => _ui(() => AddEvent(e)), AskHandoffAsync, AskTexturesAsync);
        try
        {
            var result = await Task.Run(() => _backend.RunAsync(Settings, host, _cts.Token));
            ShowResult(result);
        }
        catch (OperationCanceledException) { ShowOutcome("Stopped", "You stopped the run. Run again to continue where it left off.", false); }
        catch (Exception e) { ShowOutcome("Something went wrong", e.Message, false); }
        finally { IsBusy = false; BusyText = ""; Handoff = null; TextureQuestion = null; _cts = null; }
    }

    [RelayCommand] private void Cancel() { _cts?.Cancel(); _decision?.TrySetCanceled(); }

    [RelayCommand] private void HandoffDone() => Answer(true);
    [RelayCommand] private void HandoffPause() => Answer(false);
    [RelayCommand] private void TexturesYes() => Answer(true);
    [RelayCommand] private void TexturesNo() => Answer(false);

    [RelayCommand]
    private void BackToSetup() { Page = Page.Setup; Refresh(); }

    [RelayCommand]
    private void StartFresh() { _backend.ResetState(); PlanLines.Clear(); PlanLines.Add("Progress cleared: the next run starts from the beginning."); }

    private Task<bool> AskHandoffAsync(IReadOnlyList<M3Batch> batches, CancellationToken ct)
    {
        _decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui(() => { CurrentStep = "Your turn: install in Mod Manager"; Handoff = batches.Select(b => new M3BatchRow($"{b.Game}: {b.Title}  ({b.FileName})")).ToList(); });
        return AwaitDecision(ct, () => Handoff = null);
    }

    private Task<bool> AskTexturesAsync(string game, BackupInfo backup, CancellationToken ct)
    {
        _decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui(() => TextureQuestion = $"Install the guide's textures into {game}? This rewrites game files and takes 15 to 40 minutes. Your backup: {backup.Path}");
        return AwaitDecision(ct, () => TextureQuestion = null);
    }

    private async Task<bool> AwaitDecision(CancellationToken ct, Action clear)
    {
        using var reg = ct.Register(() => _decision?.TrySetCanceled());
        try { return await _decision!.Task; }
        finally { _ui(clear); }
    }

    private void Answer(bool yes) => _decision?.TrySetResult(yes);

    private void AddEvent(RunEvent e)
    {
        if (e.Kind == RunEventKind.StepStarted) CurrentStep = (e.Game != null ? $"{e.Game}: " : "") + e.Message;
        Log.Add(new LogLine(DateTime.Now.ToString("HH:mm:ss"), $"{(e.Game != null ? e.Game + "  " : "")}{e.Message}",
            e.Kind == RunEventKind.Error, e.Kind == RunEventKind.Warning, e.Kind == RunEventKind.StepStarted));
    }

    private void ShowResult(RunResult r)
    {
        foreach (var n in r.Notes) Notes.Add(n);
        if (r.Outcome == RunOutcome.Completed) foreach (var m in r.ManualSteps) ManualSteps.Add(new ChecklistItem(m));
        switch (r.Outcome)
        {
            case RunOutcome.Completed: ShowOutcome("All done", "Mods and textures are installed. A few steps can only be done by hand (below).", true); break;
            case RunOutcome.PausedAtM3: ShowOutcome("Paused", "Install the groups in Mod Manager, then run again to continue with the textures.", false); break;
            case RunOutcome.Blocked: ShowOutcome("Stopped before changing anything", "A check failed (see the log). Fix it and run again.", false); break;
            case RunOutcome.ContentFailed: ShowOutcome("Some mods failed", "Run again to retry; finished mods are skipped.", false); break;
            case RunOutcome.TexturesFailed: ShowOutcome("Textures failed to download", "Run again to retry; finished items are skipped.", false); break;
            default: ShowOutcome("Textures are not fully installed", "See the notes below. Run again when you are ready.", false); break;
        }
    }

    private void ShowOutcome(string title, string detail, bool ok)
    {
        ResultTitle = title; ResultDetail = detail; ResultOk = ok;
        Page = Page.Done;
    }

    // ---- helpers ----------------------------------------------------------------------------------------------
    private async Task Busy(string text, Func<CancellationToken, Task> work)
    {
        if (IsBusy) return;
        IsBusy = true; BusyText = text;
        _cts = new CancellationTokenSource();
        try { await work(_cts.Token); }
        catch (OperationCanceledException) { }
        finally { IsBusy = false; BusyText = ""; _cts = null; }
    }
}
