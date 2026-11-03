using System.Security.Cryptography;
using System.Text;
using Mele.Engine.Biq;
using Mele.Engine.Mem;
using Mele.Engine.Nexus;
using Mele.Engine.Pipeline;
using Mele.Engine.Platform;
using Mele.Engine.Textures;

namespace Mele.Engine.Orchestration;

public sealed record RunPaths(string Library, string CacheDir, string MemRoot, string GameRoot, string StateFile)
{
    public string BatchDir => Path.Combine(Library, "BatchModQueues");
    public string MflFor(string game) => Path.Combine(MemRoot, $"{game}.mfl");
}

public sealed record RunOptions(IReadOnlyList<string> Games, bool SkipTextures = false, int Jobs = 3, bool KeepArchives = false, double MinFreeGb = 40);

public enum RunOutcome
{
    Completed,
    /// <summary>A required check failed; nothing was changed.</summary>
    Blocked,
    ContentFailed,
    TexturesFailed,
    /// <summary>The user has not finished the Mod Manager step yet; run again to continue.</summary>
    PausedAtM3,
    /// <summary>The user declined (or MEM failed for) some game's texture install; see notes.</summary>
    TextureInstallIncomplete,
}

public sealed record RunResult(RunOutcome Outcome, IReadOnlyList<string> Notes, IReadOnlyList<string> ManualSteps);

public sealed record GamePlan(string Game, int Staged, int ToInstall, int Manual, bool GuideChanged, GameState State);
public sealed record RunPlan(IReadOnlyList<PreflightIssue> Issues, IReadOnlyList<GamePlan> Games);

/// <summary>
/// The whole guide, in the order that is safe: checks -> guide groups -> content mods -> textures prepared ->
/// (user installs in Mod Manager) -> MEM installs textures. Idempotent and resumable; the only destructive step is
/// the last, and it needs valid backups plus an explicit confirmation from the host.
/// </summary>
public sealed class Orchestrator(
    IGuideSource guideSource,
    ContentInstaller content,
    ITextureStage textures,
    Func<CancellationToken, Task<MemRunner>> getMem,
    IBackupLocator backups,
    Func<CancellationToken, Task<NexusUser>> validateAccount,
    GuideConfig guide,
    RunPaths paths,
    IRunHost host,
    Func<bool>? extractorAvailable = null)
{
    /// <summary>A game is expected to still be vanilla until Mod Manager has installed the batch or textures were installed.</summary>
    private bool ExpectVanilla(string game)
    {
        var gs = RunState.Load(paths.StateFile).For(game);
        return !gs.M3Confirmed && !gs.TexturesInstalled;
    }

    /// <summary>Read-only: what a run would do right now.</summary>
    public async Task<RunPlan> PlanAsync(RunOptions opts, CancellationToken ct = default)
    {
        var issues = await Preflight.RunAsync(opts.Games, paths, backups, validateAccount, opts.MinFreeGb, ct, extractorAvailable, ExpectVanilla);
        if (issues.Any(i => i.Blocking)) return new RunPlan(issues, []);
        var groups = await guideSource.FetchAsync(opts.Games, ct);
        var state = RunState.Load(paths.StateFile);
        var plans = new List<GamePlan>();
        foreach (var game in opts.Games)
        {
            var survey = ContentInstaller.Survey(groups[game].Group, paths.Library);
            var gs = state.For(game);
            plans.Add(new GamePlan(game,
                survey.Count(s => s.Status == ContentStatus.AlreadyStaged),
                survey.Count(s => s.Status == ContentStatus.WouldInstall),
                survey.Count(s => s.Status == ContentStatus.NeedsManualDownload),
                gs.ContentSignature != null && gs.ContentSignature != Signature(groups[game].Group), gs));
        }
        return new RunPlan(issues, plans);
    }

    public async Task<RunResult> RunAsync(RunOptions opts, CancellationToken ct = default)
    {
        var notes = new List<string>();
        RunResult Done(RunOutcome o) => new(o, notes, ManualStepsFor(opts.Games));

        // 1. Checks. Nothing has been changed yet.
        Step("Checks", null, RunEventKind.StepStarted, "Checking account, game folders, backups and disk space");
        var issues = await Preflight.RunAsync(opts.Games, paths, backups, validateAccount, opts.MinFreeGb, ct, extractorAvailable, ExpectVanilla);
        foreach (var i in issues) Step("Checks", null, i.Blocking ? RunEventKind.Error : RunEventKind.Warning, i.Message);
        if (issues.Any(i => i.Blocking)) { notes.Add("Blocked by the checks above; nothing was changed."); return Done(RunOutcome.Blocked); }
        Step("Checks", null, RunEventKind.StepFinished, "OK");

        // 2. The guide's current groups.
        Step("Guide", null, RunEventKind.StepStarted, "Fetching the guide's install groups from Nexus");
        var groups = await guideSource.FetchAsync(opts.Games, ct);
        Step("Guide", null, RunEventKind.StepFinished, string.Join(", ", groups.Values.Select(g => $"{g.Game}: {g.Group.Mods.Count} mods")));

        var state = RunState.Load(paths.StateFile);
        foreach (var game in opts.Games)
        {
            // The guide changed since the user last installed in M3: they must run the batch again.
            var sig = Signature(groups[game].Group);
            var gs = state.For(game);
            if (gs.ContentSignature != null && gs.ContentSignature != sig && gs.M3Confirmed)
            {
                gs.M3Confirmed = false;
                Step("Guide", game, RunEventKind.Warning, "The guide's mod list changed since you last installed it in Mod Manager; the batch must be run again.");
            }
        }

        // 3. Content mods (downloads and stages; does not touch the game).
        foreach (var game in opts.Games)
        {
            Step("Mods", game, RunEventKind.StepStarted, $"Downloading and staging {groups[game].Group.Mods.Count} mods");
            var results = await content.InstallAsync(groups[game].Group, paths.Library, opts.Jobs, opts.KeepArchives,
                onResult: r => { if (r.Status != ContentStatus.AlreadyStaged) Step("Mods", game, r.Status == ContentStatus.Failed ? RunEventKind.Error : RunEventKind.Info, $"{r.Mod.Name}: {r.Status} ({r.Note})"); }, ct: ct);
            var failed = results.Where(r => r.Status == ContentStatus.Failed).ToList();
            if (failed.Count > 0)
            {
                notes.Add($"{game}: {failed.Count} mod(s) failed to download or stage; re-run to retry (finished mods are skipped).");
                foreach (var f in failed) notes.Add($"{game}: {f.Mod.Name}: {f.Note}");
                return Done(RunOutcome.ContentFailed);
            }
            foreach (var m in results.Where(r => r.Status == ContentStatus.NeedsManualDownload))
                notes.Add($"{game}: '{m.Mod.Name}' has no download link and must be obtained manually.");
            Step("Mods", game, RunEventKind.StepFinished, $"{results.Count(r => r.Status == ContentStatus.Installed || r.Status == ContentStatus.InstalledVersionDiffers)} installed, {results.Count(r => r.Status == ContentStatus.AlreadyStaged)} already there");
        }

        // 4. Textures: fetch and stage (also not touching the game).
        if (!opts.SkipTextures)
            foreach (var game in opts.Games)
            {
                var gs = state.For(game);
                // Nothing to prepare if MEM already installed them (e.g. an adopted game), or if the files are ready on disk.
                if (gs.TexturesInstalled || (gs.TexturesPrepared && File.Exists(paths.MflFor(game))))
                {
                    Step("Textures", game, RunEventKind.Info, gs.TexturesInstalled ? "Already installed; skipping downloads" : "Already prepared; skipping downloads");
                    continue;
                }
                Step("Textures", game, RunEventKind.StepStarted, "Downloading ALOT, ISL and the guide's texture mods");
                var r = await textures.PrepareAsync(game, paths.Library, paths.MemRoot, paths.MflFor(game), opts.KeepArchives,
                    item => Step("Textures", game, item.Status == TextureStatus.Failed ? RunEventKind.Error : RunEventKind.Info, $"{item.Name}: {item.Status} ({item.Note})"),
                    null, ct);
                if (!r.Success)
                {
                    notes.Add($"{game}: texture preparation failed; re-run to retry (finished items are skipped).");
                    foreach (var f in r.Items.Where(i => i.Status == TextureStatus.Failed)) notes.Add($"{game}: {f.Name}: {f.Note}");
                    state.Save(paths.StateFile);
                    return Done(RunOutcome.TexturesFailed);
                }
                gs.TexturesPrepared = true;
                state.Save(paths.StateFile);
                Step("Textures", game, RunEventKind.StepFinished, "Ready");
            }

        // 5. Handoff: put the groups where Mod Manager's Batch Mod Installer finds them.
        Directory.CreateDirectory(paths.BatchDir);
        var batches = new List<M3Batch>();
        foreach (var game in opts.Games.Where(g => !state.For(g).M3Confirmed))
        {
            var src = groups[game].FilePath;
            File.Copy(src, Path.Combine(paths.BatchDir, Path.GetFileName(src)), overwrite: true);
            batches.Add(new M3Batch(game, $"{game} mods (guide)", Path.GetFileName(src)));
            if (!opts.SkipTextures) batches.Add(new M3Batch(game, $"{game} ALOT + ISL", $"{game} Textures M3TO (generated).biq2"));
        }
        if (batches.Count > 0)
        {
            Step("Mod Manager", null, RunEventKind.StepStarted, "Install the listed groups in Mod Manager > Batch Mod Installer, game by game");
            if (!await host.ConfirmM3BatchDoneAsync(batches, ct))
            {
                notes.Add("Paused: install the groups in Mod Manager, then run again to continue with the textures.");
                state.Save(paths.StateFile);
                return Done(RunOutcome.PausedAtM3);
            }
            foreach (var game in opts.Games.Where(g => batches.Any(b => b.Game == g)))
            {
                state.For(game).M3Confirmed = true;
                state.For(game).ContentSignature = Signature(groups[game].Group);
            }
            state.Save(paths.StateFile);
            Step("Mod Manager", null, RunEventKind.StepFinished, "Confirmed");
        }

        // 6. MEM installs the .mem textures. THE DESTRUCTIVE STEP.
        bool incomplete = false;
        if (!opts.SkipTextures)
        {
            MemRunner? mem = null; // fetched only when a game actually needs it
            foreach (var game in opts.Games)
            {
                var gs = state.For(game);
                if (gs.TexturesInstalled) { Step("MEM", game, RunEventKind.Info, "Textures already installed; skipping"); continue; }

                var backup = backups.Get(game); // checked again here, right before the game is modified
                if (!backup.Valid)
                {
                    Step("MEM", game, RunEventKind.Error, $"No valid backup ({backup.Reason}); not touching {game}");
                    notes.Add($"{game}: textures not installed (no valid backup).");
                    incomplete = true; continue;
                }
                if (!await host.ConfirmTextureInstallAsync(game, backup, ct))
                {
                    notes.Add($"{game}: textures not installed (declined). Run again when you are ready.");
                    incomplete = true; continue;
                }

                mem ??= await getMem(ct);
                Step("MEM", game, RunEventKind.StepStarted, "Installing textures (this takes 15-40 minutes)");
                var gameDir = Path.GetFullPath(paths.GameRoot);
                if (!await mem.SetGamePathAsync(game, gameDir, ct))
                {
                    Step("MEM", game, RunEventKind.Error, "MEM would not accept the game folder");
                    notes.Add($"{game}: MEM would not accept the game folder.");
                    incomplete = true; continue;
                }
                int packages = 0;
                var result = await mem.InstallAsync(game, paths.MflFor(game), line =>
                {
                    if (line.StartsWith("Package", StringComparison.Ordinal)) { if (++packages % 500 == 0) Step("MEM", game, RunEventKind.Info, $"{packages} package steps"); }
                    else if (line.Length > 0) Step("MEM", game, RunEventKind.Info, line);
                }, ct: ct);
                if (!result.Success)
                {
                    Step("MEM", game, RunEventKind.Error, $"MEM failed (exit {result.ExitCode})");
                    notes.Add($"{game}: MEM failed (exit {result.ExitCode}). Restore from your Mod Manager backup if the game misbehaves.");
                    incomplete = true; continue;
                }
                gs.TexturesInstalled = true;
                state.Save(paths.StateFile);
                Step("MEM", game, RunEventKind.StepFinished, "Textures installed");
            }
        }

        return Done(incomplete ? RunOutcome.TextureInstallIncomplete : RunOutcome.Completed);
    }

    private IReadOnlyList<string> ManualStepsFor(IReadOnlyList<string> games) =>
        games.SelectMany(g => guide.ManualSteps.TryGetValue(g, out var s) ? s.Select(x => $"{g}: {x}") : []).ToList();

    private void Step(string step, string? game, RunEventKind kind, string message) => host.Report(new RunEvent(kind, step, game, message));

    /// <summary>Fingerprint of what the guide currently installs (names and Nexus files).</summary>
    internal static string Signature(BiqGroup g) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', g.Mods.Select(m => $"{m.Name}|{m.FileId}"))))).ToLowerInvariant();
}
