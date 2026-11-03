using Mele.Engine.Biq;
using Mele.Engine.Mem;
using Mele.Engine.Nexus;
using Mele.Engine.Orchestration;
using Mele.Engine.Pipeline;
using Mele.Engine.Platform;
using Mele.Engine.Stage;
using Mele.Engine.Textures;

namespace Mele.Tests;

internal sealed class FakeGuide(Dictionary<string, GuideGroup> groups) : IGuideSource
{
    public Dictionary<string, GuideGroup> Groups { get; } = groups;
    public Task<IReadOnlyDictionary<string, GuideGroup>> FetchAsync(IEnumerable<string> games, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, GuideGroup>>(games.ToDictionary(g => g, g => Groups[g]));
}

internal sealed class FakeTextures(bool fail = false) : ITextureStage
{
    public List<string> Prepared { get; } = [];
    public Task<TextureStageResult> PrepareAsync(string game, string library, string memRoot, string mflPath, bool keep, Action<TextureResult>? onItem, IProgress<double>? p, CancellationToken ct)
    {
        Prepared.Add(game);
        if (fail) return Task.FromResult(new TextureStageResult([new TextureResult(game, 1, "ALOT", TextureStatus.Failed, "boom")], null, null));
        Directory.CreateDirectory(Path.GetDirectoryName(mflPath)!);
        File.WriteAllText(mflPath, "x.mem\r\n");
        return Task.FromResult(new TextureStageResult([new TextureResult(game, 1, "ALOT", TextureStatus.Staged, "ok")], "batch.biq2", mflPath));
    }
}

internal sealed class FakeBackups(Func<string, BackupInfo> f) : IBackupLocator { public BackupInfo Get(string game) => f(game); }

internal sealed class ScriptedHost : IRunHost
{
    public List<RunEvent> Events { get; } = [];
    public List<string> Log { get; } = [];
    public bool M3Done { get; set; } = true;
    public bool AllowTextures { get; set; } = true;
    public List<M3Batch> LastBatches { get; private set; } = [];
    public void Report(RunEvent e) { lock (Events) Events.Add(e); }
    public Task<bool> ConfirmM3BatchDoneAsync(IReadOnlyList<M3Batch> b, CancellationToken ct) { Log.Add("m3"); LastBatches = b.ToList(); return Task.FromResult(M3Done); }
    public Task<bool> ConfirmTextureInstallAsync(string game, BackupInfo backup, CancellationToken ct) { Log.Add($"confirm:{game}"); return Task.FromResult(AllowTextures); }
}

public class OrchestratorTests : IDisposable
{
    private const string Ini = "[ModInfo]\nmodname = M\n";
    private readonly TempDir _tmp = new();
    private readonly string _library, _gameRoot;
    private readonly FakeRunner _memRunner;
    private readonly ScriptedHost _host = new();
    private readonly FakeTextures _textures;
    private readonly FakeSource _source = new(_ => "a.7z");
    private bool _memSucceeds = true;
    private bool _backupsValid = true;
    private bool _premium = true;

    public OrchestratorTests()
    {
        _library = Path.Combine(_tmp.Path, "ME3TweaksModManager", "mods");
        _gameRoot = Path.Combine(_tmp.Path, "Mass Effect Legendary Edition");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(Path.Combine(_gameRoot, "Game", "ME1"));
        _textures = new FakeTextures();
        _memRunner = new FakeRunner((_, args) => args.Contains("--install-mods")
            ? (0, _memSucceeds ? ["Texture installation started...", "Installation finished."] : ["Exception occurred!"])
            : (0, []));
    }
    public void Dispose() => _tmp.Dispose();

    private static string Md5Of(string s) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private GuideGroup Guide(string game, int fileId = 1000)
    {
        var biq = Path.Combine(_tmp.Path, $"{game} Mod Collection.biq2");
        File.WriteAllText(biq, "{}");
        var mods = new[] { "Alpha", "Beta" }.Select((n, i) => new ModEntry(n, "g", 1, i + 1, $"{game}/{n}/moddesc.ini", Md5Of(Ini), 1, null)).ToList();
        return new GuideGroup(game, new BiqGroup(game, "", mods), biq, fileId);
    }

    private bool _extractorPresent = true;

    private Orchestrator Build(FakeGuide? guide = null, FakeTextures? textures = null, FakeSource? source = null)
    {
        var src = source ?? _source;
        var stager = new Stager(new FakeExtractor(new() { ["Alpha/moddesc.ini"] = Ini, ["Beta/moddesc.ini"] = Ini }));
        return new Orchestrator(
            guide ?? new FakeGuide(new() { ["LE1"] = Guide("LE1") }),
            new ContentInstaller(src, stager),
            textures ?? _textures,
            _ => Task.FromResult(new MemRunner(_memRunner, "mem")),
            new FakeBackups(g => _backupsValid ? new BackupInfo(g, "/bk", true, "OK") : new BackupInfo(g, null, false, "No backup recorded")),
            _ => Task.FromResult(new NexusUser("t", _premium)),
            new GuideConfig { GameDomain = "g", GuideModId = 1, GroupNamePattern = "{game} Mod Collection", LinkOverrides = new Dictionary<string, string>(),
                ManualSteps = new Dictionary<string, IReadOnlyList<string>> { ["LE1"] = ["Do the hair"] } },
            new RunPaths(_library, Path.Combine(_tmp.Path, "cache"), Path.Combine(_tmp.Path, "mem"), _gameRoot, Path.Combine(_tmp.Path, "state.json")),
            _host,
            () => _extractorPresent);
    }

    private static readonly RunOptions Le1 = new(["LE1"], MinFreeGb: 0);
    private IEnumerable<string> MemInstallCalls => _memRunner.Calls.Where(c => c.Args.Contains("--install-mods")).Select(c => string.Join(' ', c.Args));

    [Fact]
    public async Task Happy_path_runs_in_the_safe_order_and_installs_textures_last()
    {
        var r = await Build().RunAsync(Le1);
        Assert.Equal(RunOutcome.Completed, r.Outcome);
        Assert.Equal(["Do the hair"], r.ManualSteps.Select(s => s.Replace("LE1: ", "")));

        Assert.True(File.Exists(Path.Combine(_library, "LE1", "Alpha", "moddesc.ini")));      // content staged
        Assert.Equal(["LE1"], _textures.Prepared);                                            // textures prepared
        Assert.Equal(["m3", "confirm:LE1"], _host.Log);                                       // M3 handoff, THEN the destructive confirm
        Assert.Contains(_host.LastBatches, b => b.FileName == "LE1 Mod Collection.biq2");     // guide group handed to M3
        Assert.Contains(_host.LastBatches, b => b.FileName == "LE1 Textures M3TO (generated).biq2");
        Assert.True(File.Exists(Path.Combine(_library, "BatchModQueues", "LE1 Mod Collection.biq2"))); // and placed where M3 looks
        Assert.Single(MemInstallCalls);
        Assert.Contains("--gameid 1 --input", MemInstallCalls.Single());

        var state = RunState.Load(Path.Combine(_tmp.Path, "state.json")).For("LE1");
        Assert.True(state.TexturesPrepared && state.M3Confirmed && state.TexturesInstalled);
    }

    [Fact]
    public async Task Blocks_before_changing_anything_when_there_is_no_backup()
    {
        _backupsValid = false;
        var r = await Build().RunAsync(Le1);
        Assert.Equal(RunOutcome.Blocked, r.Outcome);
        Assert.Contains(_host.Events, e => e.Kind == RunEventKind.Error && e.Message.Contains("backup"));
        Assert.Empty(_source.Fetched);
        Assert.Empty(_textures.Prepared);
        Assert.Empty(_memRunner.Calls);
        Assert.False(File.Exists(Path.Combine(_tmp.Path, "state.json")));
    }

    [Fact]
    public async Task Blocks_for_free_accounts_and_missing_game_folders()
    {
        _premium = false;
        Assert.Equal(RunOutcome.Blocked, (await Build().RunAsync(Le1)).Outcome);
        Assert.Contains(_host.Events, e => e.Message.Contains("Premium"));

        _premium = true;
        Directory.Delete(Path.Combine(_gameRoot, "Game", "ME1"));
        Assert.Equal(RunOutcome.Blocked, (await Build().RunAsync(Le1)).Outcome);
        Assert.Contains(_host.Events, e => e.Message.Contains("game folder not found"));
    }

    [Fact]
    public async Task Blocks_with_install_instructions_when_7zip_is_missing()
    {
        _extractorPresent = false;
        var r = await Build().RunAsync(Le1);
        Assert.Equal(RunOutcome.Blocked, r.Outcome);
        Assert.Contains(_host.Events, e => e.Kind == RunEventKind.Error && e.Message.Contains("7-Zip is required"));
        Assert.Empty(_source.Fetched);            // caught before any download
        Assert.Empty(_memRunner.Calls);
    }

    [Fact]
    public async Task Pauses_at_mod_manager_and_resumes_without_redoing_downloads()
    {
        _host.M3Done = false;
        var first = await Build().RunAsync(Le1);
        Assert.Equal(RunOutcome.PausedAtM3, first.Outcome);
        Assert.Empty(MemInstallCalls);                       // textures never touched before M3 is done
        Assert.Equal(1, _textures.Prepared.Count);

        _host.M3Done = true;                                 // user finished in M3
        var second = await Build().RunAsync(Le1);
        Assert.Equal(RunOutcome.Completed, second.Outcome);
        Assert.Equal(1, _textures.Prepared.Count);           // not downloaded again
        Assert.Equal(2, _source.Fetched.Count);              // content archives fetched once each, not again
        Assert.Single(MemInstallCalls);
    }

    [Fact]
    public async Task Declining_the_texture_install_leaves_the_game_alone_and_is_retryable()
    {
        _host.AllowTextures = false;
        var r = await Build().RunAsync(Le1);
        Assert.Equal(RunOutcome.TextureInstallIncomplete, r.Outcome);
        Assert.Empty(MemInstallCalls);
        Assert.False(RunState.Load(Path.Combine(_tmp.Path, "state.json")).For("LE1").TexturesInstalled);

        _host.AllowTextures = true;
        Assert.Equal(RunOutcome.Completed, (await Build().RunAsync(Le1)).Outcome);
        Assert.Single(MemInstallCalls);
    }

    [Fact]
    public async Task A_failed_mem_run_is_reported_and_not_recorded_as_done()
    {
        _memSucceeds = false;
        var r = await Build().RunAsync(Le1);
        Assert.Equal(RunOutcome.TextureInstallIncomplete, r.Outcome);
        Assert.Contains(r.Notes, n => n.Contains("MEM failed"));
        Assert.False(RunState.Load(Path.Combine(_tmp.Path, "state.json")).For("LE1").TexturesInstalled);
    }

    [Fact]
    public async Task Warns_without_blocking_when_the_game_already_has_mods_before_the_mod_manager_step()
    {
        Directory.CreateDirectory(Path.Combine(_gameRoot, "Game", "ME1", "BioGame", "DLC", "DLC_MOD_Leftover"));
        var plan = await Build().PlanAsync(Le1);
        Assert.Contains(plan.Issues, i => !i.Blocking && i.Message.Contains("already has mods or textures"));
        Assert.Single(plan.Games);                                                // the plan still goes ahead
    }

    [Fact]
    public async Task Does_not_warn_once_the_mod_manager_step_is_done()
    {
        Directory.CreateDirectory(Path.Combine(_gameRoot, "Game", "ME1", "BioGame", "DLC", "DLC_MOD_Leftover"));
        var state = new RunState();
        state.For("LE1").M3Confirmed = true;
        state.Save(Path.Combine(_tmp.Path, "state.json"));
        var plan = await Build().PlanAsync(Le1);
        Assert.DoesNotContain(plan.Issues, i => i.Message.Contains("already has mods"));
    }

    [Fact]
    public async Task A_content_failure_stops_before_textures_and_mem()
    {
        var failing = new FakeSource(_ => throw new NexusException("HTTP 403"));
        var r = await Build(source: failing).RunAsync(Le1);
        Assert.Equal(RunOutcome.ContentFailed, r.Outcome);
        Assert.Contains(r.Notes, n => n.Contains("HTTP 403"));   // names the reason, not just a count
        Assert.Empty(_textures.Prepared);
        Assert.Empty(_host.Log);
        Assert.Empty(_memRunner.Calls);
    }

    [Fact]
    public async Task A_texture_failure_stops_before_the_mod_manager_handoff()
    {
        var r = await Build(textures: new FakeTextures(fail: true)).RunAsync(Le1);
        Assert.Equal(RunOutcome.TexturesFailed, r.Outcome);
        Assert.Empty(_host.Log);
        Assert.Empty(_memRunner.Calls);
        Assert.False(RunState.Load(Path.Combine(_tmp.Path, "state.json")).For("LE1").TexturesPrepared);
    }

    [Fact]
    public async Task A_changed_guide_invalidates_the_mod_manager_confirmation()
    {
        await Build().RunAsync(Le1);                         // completes; M3 confirmed

        var updated = Guide("LE1");
        updated = updated with { Group = updated.Group with { Mods = [.. updated.Group.Mods, new ModEntry("Gamma", "g", 1, 3, "LE1/Gamma/moddesc.ini", Md5Of(Ini), 1, null)] } };
        _host.Log.Clear();
        var r = await Build(guide: new FakeGuide(new() { ["LE1"] = updated })).RunAsync(Le1);
        Assert.Contains("m3", _host.Log);                    // asked to run the batch again
        Assert.Contains(_host.Events, e => e.Kind == RunEventKind.Warning && e.Message.Contains("changed"));
    }

    [Fact]
    public async Task An_adopted_game_downloads_no_textures_and_does_not_touch_the_game_or_ask_anything()
    {
        var state = new RunState();
        var gs = state.For("LE1");
        gs.TexturesPrepared = gs.M3Confirmed = gs.TexturesInstalled = true;   // what `state adopt` records; note: no .mfl exists on disk
        state.Save(Path.Combine(_tmp.Path, "state.json"));

        var r = await Build().RunAsync(Le1);
        Assert.Equal(RunOutcome.Completed, r.Outcome);
        Assert.Empty(_textures.Prepared);
        Assert.Empty(_host.Log);            // no Mod Manager prompt, no destructive confirmation
        Assert.Empty(_memRunner.Calls);
    }

    [Fact]
    public async Task Skip_textures_never_runs_mem_or_prepares_textures()
    {
        var r = await Build().RunAsync(Le1 with { SkipTextures = true });
        Assert.Equal(RunOutcome.Completed, r.Outcome);
        Assert.Empty(_textures.Prepared);
        Assert.Empty(_memRunner.Calls);
        Assert.DoesNotContain(_host.LastBatches, b => b.Title.Contains("ALOT"));
    }

    [Fact]
    public async Task Plan_is_read_only_and_reports_what_a_run_would_do()
    {
        var plan = await Build().PlanAsync(Le1);
        Assert.Equal(2, plan.Games.Single().ToInstall);
        Assert.Equal(0, plan.Games.Single().Staged);
        Assert.Empty(_source.Fetched);
        Assert.Empty(_textures.Prepared);
        Assert.False(Directory.Exists(Path.Combine(_library, "LE1")));
        Assert.False(File.Exists(Path.Combine(_tmp.Path, "state.json")));
    }
}
