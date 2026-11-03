using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mele.App.Services;
using Mele.Engine.Backup;
using Mele.Engine.Orchestration;
using Mele.Engine.Mem;
using Mele.Engine.Platform;
using Mele.Engine.Provision;

namespace Mele.App.ViewModels;

public enum Page { Setup, Run, Done }

public sealed record LogLine(string Time, string Text, bool IsError, bool IsWarning, bool IsHeading);
public sealed record BackupRow(string Game, bool Ok, string Text, IAsyncRelayCommand? Create = null, IAsyncRelayCommand? Restore = null, IAsyncRelayCommand? Check = null, IAsyncRelayCommand? Replace = null)
{
    public bool CanCreate => !Ok;
    public bool CanRestore => Ok;
}
public sealed record TargetRow(string Game, bool Ok, string Text);
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
    /// <summary>Whether Mod Manager's own list of game folders contains the one chosen here.</summary>
    public ObservableCollection<TargetRow> Targets { get; } = [];
    /// <summary>What the last backup, restore or check did or found, shown under the Backups card.</summary>
    public ObservableCollection<string> BackupLines { get; } = [];

    // A question on the setup page (back up / restore) that waits for a click before anything is changed.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ConfirmPending), nameof(BackupActionsEnabled))] private string? confirmTitle;
    public ObservableCollection<string> ConfirmLines { get; } = [];
    /// <summary>For a new backup the question also offers the folder to put it in.</summary>
    [ObservableProperty] private string confirmFolder = "";
    [ObservableProperty] private bool confirmFolderVisible;
    public bool ConfirmPending => ConfirmTitle != null;
    /// <summary>The row buttons are live only when nothing is running and no question is waiting.</summary>
    public bool BackupActionsEnabled => !IsBusy && !ConfirmPending;

    // ---- navigation / run state -------------------------------------------------------------------------------
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OnSetup), nameof(OnRun), nameof(OnDone))] private Page page = Page.Setup;
    public bool OnSetup => Page == Page.Setup;
    public bool OnRun => Page == Page.Run;
    public bool OnDone => Page == Page.Done;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle), nameof(BackupActionsEnabled))] private bool isBusy;
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
            var missing = s.Games.Select(g => (g, dir: GameFolders.Root(s.GameDir, g))).Where(x => !Directory.Exists(x.dir)).Select(x => x.g).ToList();
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

        Targets.Clear();
        foreach (var t in _backend.GetTargets(s)) Targets.Add(new TargetRow(t.Game, t.Ok, t.Message));

        Backups.Clear();
        foreach (var b in _backend.GetBackups(s).Where(b => s.Games.Contains(b.Game)))
        {
            var game = b.Game;
            Backups.Add(new BackupRow(game, b.Valid, b.Valid ? $"{b.Path}" : b.Reason,
                new AsyncRelayCommand(() => RunCreateBackup(game)), new AsyncRelayCommand(() => RunRestoreGame(game)), new AsyncRelayCommand(() => RunCheckGame(game)),
                new AsyncRelayCommand(() => RunCreateBackup(game, replace: true))));
        }
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

    [RelayCommand] private void ConfirmYes() => Answer(true);
    [RelayCommand] private void ConfirmNo() => Answer(false);

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

    // ---- backups ----------------------------------------------------------------------------------------------
    private bool Occupied => IsBusy || ConfirmPending;

    private async Task RunCheckGame(string game)
    {
        if (Occupied) return;
        BackupLines.Clear();
        await Busy($"Checking {game} against its backup", async ct =>
        {
            try
            {
                var report = await _backend.CheckGameAsync(Settings, game, ct);
                BackupLines.Add($"{game}: {(report.Clean ? "matches its backup and looks vanilla" : "differs from its backup or has mods installed")}");
                foreach (var l in report.Lines()) BackupLines.Add("  " + l);
            }
            catch (Exception e) when (e is not OperationCanceledException) { BackupLines.Add($"{game}: {e.Message}"); }
        });
    }

    private async Task RunCreateBackup(string game, bool replace = false)
    {
        if (Occupied) return;
        BackupLines.Clear();
        var existing = Backups.FirstOrDefault(r => r.Game == game && r.Ok)?.Text;
        string? destination = null;                 // null = the suggested folder next to the game
        BackupPlan? plan = null;
        while (true)
        {
            plan = null;
            await Busy($"Preparing a backup of {game}", async ct =>
            {
                try { plan = await _backend.PlanBackupAsync(Settings, game, destination, replace, ct); }
                catch (Exception e) when (e is BackupException or ProvisionException) { BackupLines.Add(e.Message); }
            });
            if (plan == null) return;

            List<string> lines =
            [
                $"Copy {plan.Files} files ({plan.Bytes / 1073741824.0:0.0} GB) from {plan.GameFolder}",
                "then tell Mod Manager where the backup is. This takes a few minutes and does not change the game.",
            ];
            if (replace && existing != null)
                lines.Add($"Mod Manager will use the new backup from now on. The old one ({existing}) stays on disk; delete it yourself if you no longer want it.");
            var (yes, folder) = await ConfirmAsync($"Back up {game}?", lines, plan.Destination);
            if (!yes) { BackupLines.Add("Cancelled; nothing was copied."); return; }
            if (!string.IsNullOrWhiteSpace(folder) && !SameFolder(folder, plan.Destination)) { destination = folder; continue; }   // another folder: check it, then ask again
            break;
        }

        var made = plan!;
        await Busy($"Backing up {game}", async ct =>
        {
            try
            {
                var r = await _backend.CreateBackupAsync(Settings, made, new Progress<FileProgress>(p => _ui(() => ShowProgress(p))), ct);
                BackupLines.Add(r.Success ? (r.Registered ? $"{game}: backup created, verified and registered with Mod Manager." : $"{game}: backup created and verified, but Mod Manager does not know about it yet.") : $"{game}: the backup was not created.");
                foreach (var p in r.Problems) BackupLines.Add("  " + p);
            }
            catch (OperationCanceledException) { BackupLines.Add($"{game}: stopped. The partial backup in {made.Destination} is not valid; delete it before trying again."); }
            catch (Exception e) when (e is BackupException or ProvisionException) { BackupLines.Add(e.Message); }
        });
        Refresh();
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a.Trim()), Path.TrimEndingDirectorySeparator(b.Trim()), StringComparison.Ordinal);

    private async Task RunRestoreGame(string game)
    {
        if (Occupied) return;
        BackupLines.Clear();
        RestorePlan? plan = null;
        await Busy($"Comparing {game} with its backup", async ct =>
        {
            try { plan = await _backend.PlanRestoreAsync(Settings, game, ct); }
            catch (RestoreException e) { BackupLines.Add(e.Message); }
        });
        if (plan == null) return;
        if (plan.NothingToDo) { BackupLines.Add($"{game} already matches its backup; nothing to restore."); return; }

        if (!(await ConfirmAsync($"Restore {game} from its backup?",
            [.. plan.Describe(), "Your graphics settings (GamerSettings.ini) are kept. Close the game first. The backup itself is not changed."])).Yes)
        { BackupLines.Add("Cancelled; nothing was changed."); return; }

        await Busy($"Restoring {game}", async ct =>
        {
            try
            {
                var r = await _backend.RestoreAsync(Settings, plan, new Progress<FileProgress>(p => _ui(() => ShowProgress(p))), ct);
                BackupLines.Add(r.Success ? $"{game}: restored {r.Copied} file(s) and removed {r.Deleted}. The game now matches its backup." : $"{game}: the restore did not finish cleanly.");
                foreach (var p in r.Problems) BackupLines.Add("  " + p);
            }
            catch (OperationCanceledException) { BackupLines.Add($"{game}: stopped part way. Restore again to finish."); }
            catch (RestoreException e) { BackupLines.Add(e.Message); }
        });
        Refresh();
    }

    private async Task<(bool Yes, string? Folder)> ConfirmAsync(string title, IEnumerable<string> lines, string? folder = null)
    {
        _decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui(() =>
        {
            ConfirmLines.Clear(); foreach (var l in lines) ConfirmLines.Add(l);
            ConfirmFolder = folder ?? ""; ConfirmFolderVisible = folder != null;
            ConfirmTitle = title;
        });
        try
        {
            var yes = await _decision.Task;
            return (yes, folder != null ? ConfirmFolder.Trim() : null);
        }
        finally { _ui(() => { ConfirmTitle = null; ConfirmLines.Clear(); ConfirmFolderVisible = false; }); }
    }

    private string _lastPhase = "";
    /// <summary>One line per phase and a count every 25 files: thousands of tiny updates would make the window lag.</summary>
    private void ShowProgress(FileProgress p)
    {
        if (p.Phase == _lastPhase && p.Total > 25 && p.Done % 25 != 0) return;
        _lastPhase = p.Phase;
        BusyText = p.Total > 1 ? $"{p.Phase}: {p.Done} of {p.Total}" : p.Phase;
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
