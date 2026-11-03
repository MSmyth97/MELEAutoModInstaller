using Mele.App.Services;
using Mele.Engine.Backup;
using Mele.Engine.Orchestration;
using Mele.Engine.Platform;

namespace Mele.App.Tests;

public sealed class FakeBackend : IAppBackend
{
    public bool KeySaved { get; set; }
    public bool HasSavedKey => KeySaved;
    public (bool Ok, string Message) KeyResult { get; set; } = (true, "Connected as Tester (Premium). Key saved.");
    public List<BackupInfo> BackupList { get; set; } = [new("LE1", "/b/me1", true, "OK"), new("LE2", "/b/me2", true, "OK"), new("LE3", "/b/me3", true, "OK")];
    public M3Status M3 { get; set; } = new(true, "/home/u/ME3TweaksModManager/ME3TweaksModManager.exe", true, true, false);
    public Func<IRunHost, CancellationToken, Task<RunResult>> RunScript { get; set; } =
        (_, _) => Task.FromResult(new RunResult(RunOutcome.Completed, [], []));
    public RunPlan Plan { get; set; } = new([], []);
    public int SetupCalls, LaunchCalls, ResetCalls;
    public string StatePath => "/tmp/state.json";

    public Task<(bool Ok, string Message)> CheckKeyAsync(string key, CancellationToken ct) { KeySaved = KeyResult.Ok || KeySaved; return Task.FromResult(KeyResult); }
    public IReadOnlyList<BackupInfo> GetBackups(AppSettings s) => BackupList;
    public M3Status GetM3Status(AppSettings s) => M3;
    public Task SetupM3Async(AppSettings s, IProgress<string> progress, CancellationToken ct) { SetupCalls++; M3 = M3 with { Installed = true }; return Task.CompletedTask; }
    public void LaunchM3(AppSettings s) => LaunchCalls++;
    public Task<RunPlan> PlanAsync(AppSettings s, IRunHost host, CancellationToken ct) => Task.FromResult(Plan);
    public Task<RunResult> RunAsync(AppSettings s, IRunHost host, CancellationToken ct) => RunScript(host, ct);
    public void ResetState() => ResetCalls++;

    // Backups: each call can be scripted; the defaults describe a game that matches its backup and needs nothing.
    public Func<string, GameReport> Check { get; set; } = g => new GameReport(g, "/g/" + g, "/b/" + g, new VanillaReport([], []),
        new CompareReport([], [], [], new HashSet<string> { "INT" }, 3684, 3684), null);
    public Func<string, BackupPlan> BackupPlanFor { get; set; } = g => new BackupPlan(g, "/g/" + g, "/b/new " + g, 3684, 16_000_000_000);
    public Func<BackupPlan, BackupResult> CreateResult { get; set; } = _ => new BackupResult(true, true, []);
    public Func<string, RestorePlan> RestorePlanFor { get; set; } = g => new RestorePlan(g, "/b/" + g, "/g/" + g,
        new CompareReport(["a.pcc"], [new SizeDiff("b.pcc", 1, 2)], [], new HashSet<string> { "INT" }, 10, 12), ["a.pcc", "b.pcc"], ["DLC.pcc"], ["x_DE.pcc"], [], 2048);
    public Func<RestorePlan, RestoreResult> RestoreResultFor { get; set; } = _ => new RestoreResult(true, 2, 1, [], null);
    public Exception? PlanFailure { get; set; }
    public TaskCompletionSource? HoldCreate { get; set; }
    public List<string> Calls { get; } = [];
    public List<BackupPlan> Created { get; } = [];

    public Task<GameReport> CheckGameAsync(AppSettings s, string game, CancellationToken ct) { Calls.Add("check " + game); return Task.FromResult(Check(game)); }
    public List<(string Game, string? Destination, bool Replace)> PlanRequests { get; } = [];
    /// <summary>Lets a test refuse particular destinations (for example one that is not empty).</summary>
    public Func<string?, Exception?>? PlanFailureForDestination { get; set; }
    public Task<BackupPlan> PlanBackupAsync(AppSettings s, string game, string? destination, bool replaceExisting, CancellationToken ct)
    {
        Calls.Add("plan-backup " + game);
        PlanRequests.Add((game, destination, replaceExisting));
        var failure = PlanFailure ?? PlanFailureForDestination?.Invoke(destination);
        if (failure != null) return Task.FromException<BackupPlan>(failure);
        var plan = BackupPlanFor(game);
        return Task.FromResult(destination == null ? plan : plan with { Destination = destination });
    }
    public async Task<BackupResult> CreateBackupAsync(AppSettings s, BackupPlan plan, IProgress<FileProgress> progress, CancellationToken ct)
    {
        Calls.Add("create " + plan.Game);
        Created.Add(plan);
        progress.Report(new("Copying the game", 25, 3684, "x"));
        if (HoldCreate != null) await HoldCreate.Task.WaitAsync(ct);
        return CreateResult(plan);
    }
    public Task<RestorePlan> PlanRestoreAsync(AppSettings s, string game, CancellationToken ct)
    {
        Calls.Add("plan-restore " + game);
        return PlanFailure != null ? Task.FromException<RestorePlan>(PlanFailure) : Task.FromResult(RestorePlanFor(game));
    }
    public Task<RestoreResult> RestoreAsync(AppSettings s, RestorePlan plan, IProgress<FileProgress> progress, CancellationToken ct)
    {
        Calls.Add("restore " + plan.Game);
        return Task.FromResult(RestoreResultFor(plan));
    }
}
