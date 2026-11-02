using Mele.App.Services;
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
}
