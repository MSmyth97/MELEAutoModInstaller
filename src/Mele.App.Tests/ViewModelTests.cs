using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mele.App.ViewModels;
using Mele.Engine.Backup;
using Mele.Engine.Provision;
using Mele.Engine.Orchestration;
using Mele.Engine.Platform;

namespace Mele.App.Tests;

public class ViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mele-gui-" + Guid.NewGuid().ToString("N"));
    private readonly string _settingsFile;
    public ViewModelTests() { Directory.CreateDirectory(_root); _settingsFile = Path.Combine(_root, "settings.json"); }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>A folder that looks like a Legendary Edition install.</summary>
    internal string FakeGame()
    {
        var g = Path.Combine(_root, "Mass Effect Legendary Edition");
        foreach (var n in new[] { "ME1", "ME2", "ME3" }) Directory.CreateDirectory(Path.Combine(g, "Game", n));
        return g;
    }

    internal MainViewModel Vm(FakeBackend b, string? gameDir = null, Action<Action>? ui = null) =>
        new(b, new AppSettings { GameDir = gameDir ?? FakeGame() }, ui, _settingsFile);

    internal static async Task WaitFor(Func<bool> cond, int ms = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("condition not met");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    [AvaloniaFact]
    public void Start_is_only_allowed_with_a_real_game_folder_and_a_game_selected()
    {
        var vm = new MainViewModel(new FakeBackend(), new AppSettings(), settingsFile: _settingsFile);
        Assert.False(vm.CanStart);                                   // nothing chosen yet
        vm.GameDir = Path.Combine(_root, "nope");
        Assert.Contains("does not exist", vm.GameDirStatus);
        vm.GameDir = FakeGame();
        Assert.True(vm.CanStart);
        vm.Le1 = vm.Le2 = vm.Le3 = false;
        Assert.False(vm.CanStart);
        Assert.Contains("at least one game", vm.GameDirStatus);
        vm.Le2 = true;
        Directory.Delete(Path.Combine(vm.GameDir, "Game", "ME2"));
        vm.Refresh();
        Assert.False(vm.CanStart);
        Assert.Contains("LE2", vm.GameDirStatus);
        vm.StartCommand.Execute(null);                              // Start re-checks the disk itself and refuses
        Assert.Equal(Page.Setup, vm.Page);
    }

    [AvaloniaFact]
    public async Task Saving_a_good_key_reports_success_and_clears_the_text_box()
    {
        var b = new FakeBackend();
        var vm = Vm(b);
        vm.ApiKeyInput = "  secret-key  ";
        await vm.SaveKeyCommand.ExecuteAsync(null);
        Assert.True(vm.KeyOk);
        Assert.Contains("Premium", vm.KeyStatus);
        Assert.Equal("", vm.ApiKeyInput);                            // the key is not kept on screen
        Assert.DoesNotContain("secret-key", vm.KeyStatus);
    }

    [AvaloniaFact]
    public async Task A_rejected_key_is_explained_and_an_empty_one_is_not_even_sent()
    {
        var b = new FakeBackend { KeyResult = (false, "Nexus did not accept that key (HTTP 401).") };
        var vm = Vm(b);
        await vm.SaveKeyCommand.ExecuteAsync(null);
        Assert.False(vm.KeyOk);
        Assert.Contains("Paste your Nexus", vm.KeyStatus);
        Assert.False(b.KeySaved);
        vm.ApiKeyInput = "bad";
        await vm.SaveKeyCommand.ExecuteAsync(null);
        Assert.Contains("did not accept", vm.KeyStatus);
        Assert.Equal("bad", vm.ApiKeyInput);                         // kept so the user can fix a typo
    }

    [AvaloniaFact]
    public async Task Setting_up_mod_manager_refreshes_the_status()
    {
        var b = new FakeBackend { M3 = new(false, "/m3/ME3TweaksModManager.exe", false, true, false) };
        var vm = Vm(b);
        Assert.False(vm.M3Ready);
        Assert.Contains("Not installed", vm.M3Status);
        await vm.SetupM3Command.ExecuteAsync(null);
        Assert.Equal(1, b.SetupCalls);
        Assert.True(vm.M3Ready);
        Assert.Contains("Installed", vm.M3Status);
    }

    [AvaloniaFact]
    public void A_missing_umu_is_explained_on_linux()
    {
        var vm = Vm(new FakeBackend { M3 = new(false, "/x", false, true, UmuMissing: true) });
        Assert.Contains("umu", vm.M3Status);
    }

    [AvaloniaFact]
    public void Backup_rows_only_cover_the_selected_games()
    {
        var b = new FakeBackend { BackupList = [new("LE1", "/b1", true, "OK"), new("LE2", null, false, "No backup recorded by Mod Manager"), new("LE3", "/b3", true, "OK")] };
        var vm = Vm(b);
        Assert.Equal(["LE1", "LE2", "LE3"], vm.Backups.Select(x => x.Game));
        Assert.False(vm.Backups[1].Ok);
        vm.Le2 = false;
        Assert.Equal(["LE1", "LE3"], vm.Backups.Select(x => x.Game));
    }

    [AvaloniaFact]
    public async Task Check_lists_problems_without_starting_a_run()
    {
        var b = new FakeBackend { Plan = new RunPlan([new PreflightIssue(true, "LE1: no valid Mod Manager backup")], []) };
        var vm = Vm(b);
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Contains(vm.PlanLines, l => l.StartsWith("Problem:") && l.Contains("backup"));
        Assert.Equal(Page.Setup, vm.Page);
    }

    [AvaloniaFact]
    public async Task Full_run_asks_at_both_decision_points_and_ends_on_the_done_page()
    {
        var b = new FakeBackend();
        b.RunScript = async (host, ct) =>
        {
            host.Report(new RunEvent(RunEventKind.StepStarted, "Mods", "LE1", "Downloading and staging 63 mods"));
            host.Report(new RunEvent(RunEventKind.Info, "Mods", "LE1", "Community Patch: Installed (downloaded)"));
            var m3 = await host.ConfirmM3BatchDoneAsync([new M3Batch("LE1", "LE1 mods (guide)", "LE1 Mod Collection.biq2")], ct);
            var tex = await host.ConfirmTextureInstallAsync("LE1", new BackupInfo("LE1", "/b/me1", true, "OK"), ct);
            return new RunResult(m3 && tex ? RunOutcome.Completed : RunOutcome.TextureInstallIncomplete, ["a note"], ["LE1: Do the hair"]);
        };
        var vm = Vm(b, ui: a => Dispatcher.UIThread.Post(a));
        var run = vm.StartCommand.ExecuteAsync(null);

        await WaitFor(() => vm.HandoffPending);
        Assert.Equal(Page.Run, vm.Page);
        Assert.Contains("LE1: LE1 mods (guide)", vm.Handoff![0].Display);
        Assert.Contains(vm.Log, l => l.Text.Contains("Community Patch"));
        Assert.Equal("Your turn: install in Mod Manager", vm.CurrentStep);
        vm.HandoffDoneCommand.Execute(null);

        await WaitFor(() => vm.TextureQuestionPending);
        Assert.False(vm.HandoffPending);
        Assert.Contains("rewrites game files", vm.TextureQuestion);
        vm.TexturesYesCommand.Execute(null);

        await run;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Page.Done, vm.Page);
        Assert.True(vm.ResultOk);
        Assert.Equal("All done", vm.ResultTitle);
        Assert.Equal(["LE1: Do the hair"], vm.ManualSteps.Select(m => m.Text));
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task Pausing_at_the_handoff_and_declining_textures_are_reported_as_not_done()
    {
        var b = new FakeBackend { RunScript = async (host, ct) =>
        {
            var m3 = await host.ConfirmM3BatchDoneAsync([new M3Batch("LE1", "t", "f")], ct);
            return new RunResult(m3 ? RunOutcome.Completed : RunOutcome.PausedAtM3, ["Paused: install the groups in Mod Manager"], ["LE1: manual"]);
        } };
        var vm = Vm(b, ui: a => Dispatcher.UIThread.Post(a));
        var run = vm.StartCommand.ExecuteAsync(null);
        await WaitFor(() => vm.HandoffPending);
        vm.HandoffPauseCommand.Execute(null);
        await run; Dispatcher.UIThread.RunJobs();
        Assert.Equal("Paused", vm.ResultTitle);
        Assert.False(vm.ResultOk);
        Assert.Empty(vm.ManualSteps);                                 // the hand-done checklist only appears when everything finished
        Assert.Single(vm.Notes);
    }

    [AvaloniaFact]
    public async Task Stop_cancels_a_waiting_question_and_ends_cleanly()
    {
        var b = new FakeBackend { RunScript = async (host, ct) => { await host.ConfirmM3BatchDoneAsync([new M3Batch("LE1", "t", "f")], ct); return new RunResult(RunOutcome.Completed, [], []); } };
        var vm = Vm(b, ui: a => Dispatcher.UIThread.Post(a));
        var run = vm.StartCommand.ExecuteAsync(null);
        await WaitFor(() => vm.HandoffPending);
        vm.CancelCommand.Execute(null);
        await run; Dispatcher.UIThread.RunJobs();
        Assert.Equal("Stopped", vm.ResultTitle);
        Assert.False(vm.HandoffPending);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task An_unexpected_exception_in_the_run_is_shown_not_swallowed()
    {
        var vm = Vm(new FakeBackend { RunScript = (_, _) => throw new InvalidOperationException("disk exploded") });
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("Something went wrong", vm.ResultTitle);
        Assert.Contains("disk exploded", vm.ResultDetail);
    }

    [AvaloniaFact]
    public async Task Settings_are_saved_when_a_run_starts_and_restored_next_time()
    {
        var vm = Vm(new FakeBackend());
        vm.Le3 = false; vm.IncludeTextures = false;
        await vm.StartCommand.ExecuteAsync(null);
        var back = AppSettings.Load(_settingsFile);
        Assert.Equal(vm.GameDir, back.GameDir);
        Assert.False(back.Le3);
        Assert.False(back.IncludeTextures);
    }

    [AvaloniaFact]
    public void Forget_progress_resets_the_backend_state()
    {
        var b = new FakeBackend();
        Vm(b).StartFreshCommand.Execute(null);
        Assert.Equal(1, b.ResetCalls);
    }

    // ---- backups: create, restore, check -----------------------------------------------------------------------

    private static BackupRow Row(MainViewModel vm, string game) => vm.Backups.Single(r => r.Game == game);

    [AvaloniaFact]
    public void A_game_without_a_backup_offers_to_make_one_and_a_game_with_one_offers_check_and_restore()
    {
        var b = new FakeBackend { BackupList = [new("LE1", null, false, "No backup recorded"), new("LE2", "/b/me2", true, "OK"), new("LE3", null, false, "x")] };
        var vm = Vm(b);
        Assert.True(Row(vm, "LE1").CanCreate);
        Assert.False(Row(vm, "LE1").CanRestore);
        Assert.True(Row(vm, "LE2").CanRestore);
        Assert.False(Row(vm, "LE2").CanCreate);
    }

    [AvaloniaFact]
    public async Task Making_a_backup_shows_what_will_happen_and_waits_for_a_yes_before_copying()
    {
        var b = new FakeBackend { BackupList = [new("LE1", null, false, "No backup recorded"), new("LE2", "/b", true, "OK"), new("LE3", "/b", true, "OK")] };
        var vm = Vm(b, ui: a => Dispatcher.UIThread.Post(a));
        var task = Row(vm, "LE1").Create!.ExecuteAsync(null);

        await WaitFor(() => vm.ConfirmPending);
        Assert.Equal("Back up LE1?", vm.ConfirmTitle);
        Assert.Contains(vm.ConfirmLines, l => l.Contains("3684 files") && l.Contains("/g/LE1"));
        Assert.Equal("/b/new LE1", vm.ConfirmFolder);                              // the destination is in the editable folder box
        Assert.DoesNotContain("create LE1", b.Calls);                              // nothing is copied before the click

        vm.ConfirmYesCommand.Execute(null);
        await task;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("create LE1", b.Calls);
        Assert.False(vm.ConfirmPending);
        Assert.Contains(vm.BackupLines, l => l.Contains("backup created, verified and registered"));
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task Declining_the_backup_copies_nothing()
    {
        var b = new FakeBackend { BackupList = [new("LE1", null, false, "x"), new("LE2", "/b", true, "OK"), new("LE3", "/b", true, "OK")] };
        var vm = Vm(b, ui: a => Dispatcher.UIThread.Post(a));
        var task = Row(vm, "LE1").Create!.ExecuteAsync(null);
        await WaitFor(() => vm.ConfirmPending);
        vm.ConfirmNoCommand.Execute(null);
        await task;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain("create LE1", b.Calls);
        Assert.Contains(vm.BackupLines, l => l.Contains("nothing was copied"));
    }

    [AvaloniaFact]
    public async Task A_refused_backup_shows_the_reason_and_never_asks()
    {
        var b = new FakeBackend { BackupList = [new("LE1", null, false, "x"), new("LE2", "/b", true, "OK"), new("LE3", "/b", true, "OK")],
            PlanFailure = new BackupException("LE1: the game is not vanilla (53 mod folder(s) in BioGame/DLC). Restore the game first.") };
        var vm = Vm(b);
        await Row(vm, "LE1").Create!.ExecuteAsync(null);
        Assert.False(vm.ConfirmPending);
        Assert.Contains(vm.BackupLines, l => l.Contains("not vanilla"));
        Assert.DoesNotContain("create LE1", b.Calls);
    }

    [AvaloniaFact]
    public async Task A_backup_that_could_not_be_registered_says_so_and_lists_the_problem()
    {
        var b = new FakeBackend { BackupList = [new("LE1", null, false, "x"), new("LE2", "/b", true, "OK"), new("LE3", "/b", true, "OK")],
            CreateResult = _ => new BackupResult(true, false, ["The backup is complete but Mod Manager does not know about it yet (reg exit 1)."]) };
        var vm = Vm(b, ui: a => Dispatcher.UIThread.Post(a));
        var task = Row(vm, "LE1").Create!.ExecuteAsync(null);
        await WaitFor(() => vm.ConfirmPending);
        vm.ConfirmYesCommand.Execute(null);
        await task;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(vm.BackupLines, l => l.Contains("Mod Manager does not know about it yet.") || l.Contains("does not know about it yet"));
        Assert.Contains(vm.BackupLines, l => l.Contains("reg exit 1"));
    }

    [AvaloniaFact]
    public async Task Restore_shows_the_plan_waits_for_a_yes_then_reports_the_result_and_refreshes()
    {
        var b = new FakeBackend();
        var vm = Vm(b, ui: a => Dispatcher.UIThread.Post(a));
        var task = Row(vm, "LE1").Restore!.ExecuteAsync(null);

        await WaitFor(() => vm.ConfirmPending);
        Assert.Equal("Restore LE1 from its backup?", vm.ConfirmTitle);
        Assert.Contains(vm.ConfirmLines, l => l.StartsWith("Copy 2 file(s)"));
        Assert.Contains(vm.ConfirmLines, l => l.StartsWith("Keep 1 vanilla file(s)"));
        Assert.Contains(vm.ConfirmLines, l => l.Contains("GamerSettings.ini"));
        Assert.DoesNotContain("restore LE1", b.Calls);

        vm.ConfirmYesCommand.Execute(null);
        await task;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("restore LE1", b.Calls);
        Assert.Contains(vm.BackupLines, l => l.Contains("restored 2 file(s) and removed 1"));
    }

    [AvaloniaFact]
    public async Task Restore_failures_are_listed_not_hidden()
    {
        var b = new FakeBackend { RestoreResultFor = _ => new RestoreResult(false, 1, 0, ["Could not delete x.pcc: access denied"], null) };
        var vm = Vm(b, ui: a => Dispatcher.UIThread.Post(a));
        var task = Row(vm, "LE1").Restore!.ExecuteAsync(null);
        await WaitFor(() => vm.ConfirmPending);
        vm.ConfirmYesCommand.Execute(null);
        await task;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(vm.BackupLines, l => l.Contains("did not finish cleanly"));
        Assert.Contains(vm.BackupLines, l => l.Contains("access denied"));
    }

    [AvaloniaFact]
    public async Task Restore_says_so_when_the_game_already_matches_and_asks_nothing()
    {
        var b = new FakeBackend { RestorePlanFor = g => new RestorePlan(g, "/b", "/g", new CompareReport([], [], [], new HashSet<string>(), 1, 1), [], [], [], [], 0) };
        var vm = Vm(b);
        await Row(vm, "LE1").Restore!.ExecuteAsync(null);
        Assert.False(vm.ConfirmPending);
        Assert.Contains(vm.BackupLines, l => l.Contains("already matches its backup"));
        Assert.DoesNotContain("restore LE1", b.Calls);
    }

    [AvaloniaFact]
    public async Task A_restore_the_engine_refuses_shows_its_message()
    {
        var b = new FakeBackend { PlanFailure = new RestoreException("LE1: there is no valid backup to restore from (No backup recorded).") };
        var vm = Vm(b);
        await Row(vm, "LE1").Restore!.ExecuteAsync(null);
        Assert.Contains(vm.BackupLines, l => l.Contains("no valid backup to restore from"));
        Assert.False(vm.ConfirmPending);
    }

    [AvaloniaFact]
    public async Task Check_lists_what_the_comparison_found()
    {
        var b = new FakeBackend { Check = g => new GameReport(g, "/g", "/b", new VanillaReport(["53 mod folder(s) in BioGame/DLC"], ["ASI mods are installed"]),
            new CompareReport([], [new SizeDiff("a", 1, 2)], [new ExtraFile("BioGame/DLC/DLC_MOD_X/a.pcc", ExtraKind.Mod), new ExtraFile("x_DE.pcc", ExtraKind.Localised)], new HashSet<string> { "INT" }, 5, 7), null) };
        var vm = Vm(b);
        await Row(vm, "LE1").Check!.ExecuteAsync(null);
        Assert.Contains(vm.BackupLines, l => l.Contains("differs from its backup or has mods installed"));
        Assert.Contains(vm.BackupLines, l => l.Contains("NOT vanilla") && l.Contains("53 mod folder"));
        Assert.Contains(vm.BackupLines, l => l.Contains("localised vanilla files 1"));
    }

    [AvaloniaFact]
    public async Task While_a_backup_is_being_made_other_backup_actions_are_ignored()
    {
        var hold = new TaskCompletionSource();
        var b = new FakeBackend { BackupList = [new("LE1", null, false, "x"), new("LE2", "/b", true, "OK"), new("LE3", "/b", true, "OK")], HoldCreate = hold };
        var vm = Vm(b, ui: a => Dispatcher.UIThread.Post(a));
        var task = Row(vm, "LE1").Create!.ExecuteAsync(null);
        await WaitFor(() => vm.ConfirmPending);
        await Row(vm, "LE2").Restore!.ExecuteAsync(null);                         // ignored while a question is open
        Assert.DoesNotContain("plan-restore LE2", b.Calls);
        Assert.False(vm.BackupActionsEnabled);                                     // the row buttons are dimmed while a question is open
        vm.ConfirmYesCommand.Execute(null);
        await WaitFor(() => vm.IsBusy && vm.BusyText.Contains("25 of 3684"));    // progress is shown while copying
        Assert.False(vm.BackupActionsEnabled);                                     // and while it works
        await Row(vm, "LE2").Restore!.ExecuteAsync(null);                         // ignored while busy
        Assert.DoesNotContain("plan-restore LE2", b.Calls);
        hold.SetResult();
        await task;
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsBusy);
        Assert.True(vm.BackupActionsEnabled);
    }

    // ---- a new backup for a game that already has one, and choosing the folder --------------------------------------

    [AvaloniaFact]
    public void Games_with_a_backup_offer_a_new_one_and_games_without_do_not_need_it()
    {
        var b = new FakeBackend { BackupList = [new("LE1", null, false, "none"), new("LE2", "/b/me2", true, "OK"), new("LE3", "/b/me3", true, "OK")] };
        var vm = Vm(b);
        Assert.NotNull(Row(vm, "LE2").Replace);
        Assert.True(Row(vm, "LE2").CanRestore);          // the New backup... button shows with Check and Restore
        Assert.True(Row(vm, "LE1").CanCreate);
    }

    [AvaloniaFact]
    public async Task A_new_backup_for_a_game_that_has_one_says_the_old_one_is_kept_and_offers_the_folder()
    {
        var b = new FakeBackend();
        var vm = Vm(b, ui: a => Dispatcher.UIThread.Post(a));
        var task = Row(vm, "LE1").Replace!.ExecuteAsync(null);

        await WaitFor(() => vm.ConfirmPending);
        Assert.Equal([("LE1", (string?)null, true)], b.PlanRequests);                        // replace was requested, folder left to the app
        Assert.True(vm.ConfirmFolderVisible);
        Assert.Equal("/b/new LE1", vm.ConfirmFolder);
        Assert.Contains(vm.ConfirmLines, l => l.Contains("The old one (/b/me1) stays on disk"));

        vm.ConfirmYesCommand.Execute(null);
        await task;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("/b/new LE1", Assert.Single(b.Created).Destination);
        Assert.False(vm.ConfirmFolderVisible);
    }

    [AvaloniaFact]
    public async Task A_first_backup_does_not_talk_about_an_old_one()
    {
        var b = new FakeBackend { BackupList = [new("LE1", null, false, "none"), new("LE2", "/b", true, "OK"), new("LE3", "/b", true, "OK")] };
        var vm = Vm(b, ui: a => Dispatcher.UIThread.Post(a));
        var task = Row(vm, "LE1").Create!.ExecuteAsync(null);
        await WaitFor(() => vm.ConfirmPending);
        Assert.DoesNotContain(vm.ConfirmLines, l => l.Contains("old one"));
        Assert.False(b.PlanRequests.Single().Replace);
        vm.ConfirmNoCommand.Execute(null);
        await task;
    }

    [AvaloniaFact]
    public async Task Changing_the_folder_checks_the_new_one_and_asks_again_before_copying()
    {
        var b = new FakeBackend();
        var vm = Vm(b, ui: a => Dispatcher.UIThread.Post(a));
        var task = Row(vm, "LE1").Replace!.ExecuteAsync(null);
        await WaitFor(() => vm.ConfirmPending);

        vm.ConfirmFolder = "/mnt/big/ME1 Full Backup";
        vm.ConfirmYesCommand.Execute(null);                                                  // "yes" with a different folder = check that folder first
        await WaitFor(() => b.PlanRequests.Count == 2 && vm.ConfirmPending && vm.ConfirmFolder == "/mnt/big/ME1 Full Backup");
        Assert.Equal("/mnt/big/ME1 Full Backup", b.PlanRequests[1].Destination);
        Assert.Empty(b.Created);                                                             // still nothing copied

        vm.ConfirmYesCommand.Execute(null);
        await task;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("/mnt/big/ME1 Full Backup", Assert.Single(b.Created).Destination);
    }

    [AvaloniaFact]
    public async Task A_folder_the_engine_refuses_is_reported_and_nothing_is_copied()
    {
        var b = new FakeBackend { PlanFailureForDestination = d => d == "/not/empty" ? new BackupException("LE1: the backup folder /not/empty is not empty. Choose an empty or new folder.") : null };
        var vm = Vm(b, ui: a => Dispatcher.UIThread.Post(a));
        var task = Row(vm, "LE1").Replace!.ExecuteAsync(null);
        await WaitFor(() => vm.ConfirmPending);
        vm.ConfirmFolder = "/not/empty";
        vm.ConfirmYesCommand.Execute(null);
        await task;
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.ConfirmPending);
        Assert.Contains(vm.BackupLines, l => l.Contains("is not empty"));
        Assert.Empty(b.Created);
    }

    [AvaloniaFact]
    public async Task Restore_does_not_show_a_folder_box()
    {
        var vm = Vm(new FakeBackend(), ui: a => Dispatcher.UIThread.Post(a));
        var task = Row(vm, "LE1").Restore!.ExecuteAsync(null);
        await WaitFor(() => vm.ConfirmPending);
        Assert.False(vm.ConfirmFolderVisible);
        vm.ConfirmNoCommand.Execute(null);
        await task;
    }

    [AvaloniaFact]
    public void The_mod_manager_card_lists_whether_each_selected_game_is_a_target()
    {
        var b = new FakeBackend { TargetList = [
            new("LE1", TargetStatus.Matches, "Mod Manager's target for LE1 is this game folder.", @"Z:\g1", []),
            new("LE2", TargetStatus.Missing, @"Mod Manager's target for LE2 (X:\home\u) does not exist. Add the game again with this path: Z:\g2", @"Z:\g2", []),
            new("LE3", TargetStatus.NotRegistered, "no target", @"Z:\g3", [])] };
        var vm = Vm(b);
        Assert.Equal(["LE1", "LE2", "LE3"], vm.Targets.Select(t => t.Game));
        Assert.True(vm.Targets[0].Ok);
        Assert.False(vm.Targets[1].Ok);
        Assert.Contains(@"Z:\g2", vm.Targets[1].Text);
        vm.Le3 = false;                                                                         // only the selected games are shown
        Assert.Equal(["LE1", "LE2"], vm.Targets.Select(t => t.Game));
    }

    [AvaloniaFact]
    public void No_targets_are_shown_until_mod_manager_is_set_up()
    {
        var vm = Vm(new FakeBackend());
        Assert.Empty(vm.Targets);
    }
}
