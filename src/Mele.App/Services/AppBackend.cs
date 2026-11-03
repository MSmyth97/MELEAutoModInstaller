using System.Diagnostics;
using Mele.Engine.Archive;
using Mele.Engine.Backup;
using Mele.Engine.Mem;
using Mele.Engine.Nexus;
using Mele.Engine.Orchestration;
using Mele.Engine.Platform;
using Mele.Engine.Provision;

namespace Mele.App.Services;

/// <summary>The production backend: real network, real disk, real Mod Manager.</summary>
public sealed class AppBackend : IAppBackend
{
    public string StatePath => Path.Combine(AppPaths.DataDir, "state.json");

    public bool HasSavedKey => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEXUS_API_KEY")) || ApiKeyStore.CandidateFiles().Any(File.Exists);

    public async Task<(bool Ok, string Message)> CheckKeyAsync(string key, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient();
            var me = await new NexusClient(http, key.Trim()).ValidateAsync(ct);
            ApiKeyStore.Save(key); // Nexus accepted it; keep it (owner-only file) even if the account turns out not to be Premium
            return me.IsPremium
                ? (true, $"Connected as {me.Name} (Premium). Key saved.")
                : (false, $"Connected as {me.Name}, but this account is not Premium. Downloads through the API need Premium, so the installer cannot run with it.");
        }
        catch (NexusException e) { return (false, $"Nexus did not accept that key ({e.Message})."); }
        catch (HttpRequestException e) { return (false, $"Could not reach Nexus: {e.Message}"); }
        catch (ApiKeyException e) { return (false, e.Message); }
    }

    public IReadOnlyList<BackupInfo> GetBackups(AppSettings s)
    {
        var loc = M3Location.Resolve(s.PrefixDir);
        return new[] { "LE1", "LE2", "LE3" }.Select(loc.Backups.Get).ToList();
    }

    public Task<GameReport> CheckGameAsync(AppSettings s, string game, CancellationToken ct)
    {
        var backup = M3Location.Resolve(s.PrefixDir).Backups.Get(game);
        return Task.Run(() => GameReport.Build(game, GameFolders.Root(s.GameDir, game), backup, ct), ct);
    }

    public Task<BackupPlan> PlanBackupAsync(AppSettings s, string game, CancellationToken ct)
    {
        var loc = M3Location.Resolve(s.PrefixDir);
        var creator = Creator(loc);
        return Task.Run(() => creator.Prepare(game, GameFolders.Root(s.GameDir, game), BackupCreator.DefaultDestination(s.GameDir, game), loc.Backups.Get(game), ct: ct), ct);
    }

    public Task<BackupResult> CreateBackupAsync(AppSettings s, BackupPlan plan, IProgress<FileProgress> progress, CancellationToken ct) =>
        Creator(M3Location.Resolve(s.PrefixDir)).CreateAsync(plan, progress, ct);

    public Task<RestorePlan> PlanRestoreAsync(AppSettings s, string game, CancellationToken ct)
    {
        var backup = M3Location.Resolve(s.PrefixDir).Backups.Get(game);
        return Task.Run(() => new GameRestorer().Prepare(game, backup, GameFolders.Root(s.GameDir, game), ct: ct), ct);
    }

    public Task<RestoreResult> RestoreAsync(AppSettings s, RestorePlan plan, IProgress<FileProgress> progress, CancellationToken ct) =>
        new GameRestorer().RestoreAsync(plan, progress, ct);

    /// <summary>How a new backup is announced to Mod Manager: the registry on Windows, Wine's registry in the app's prefix on Linux.</summary>
    private static BackupCreator Creator(M3Location loc)
    {
        if (OperatingSystem.IsWindows()) return new BackupCreator(new WindowsBackupRegistry());
        if (loc.Prefix is not { IsInitialized: true } prefix)
            throw new BackupException("Set up Mod Manager first (step 3): its Wine prefix is where the backup has to be registered.");
        return new BackupCreator(new WineBackupRegistry(prefix));
    }

    public M3Status GetM3Status(AppSettings s)
    {
        var loc = M3Location.Resolve(s.PrefixDir);
        return new M3Status(loc.IsInstalled, loc.ExePath, loc.Prefix?.IsInitialized ?? true, loc.Prefix != null,
            !OperatingSystem.IsWindows() && ProtonPrefix.FindUmu() == null);
    }

    public async Task SetupM3Async(AppSettings s, IProgress<string> progress, CancellationToken ct)
    {
        var loc = M3Location.Resolve(s.PrefixDir);
        if (loc.Prefix is { } prefix && !prefix.IsInitialized)
        {
            progress.Report("Creating the Proton prefix (the first time takes a minute)");
            await prefix.EnsureAsync(null, ct);
            loc = M3Location.Resolve(s.PrefixDir); // the profile folder exists only now
        }
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        await new M3Installer(http, new SevenZipExtractor(), loc.Home).EnsureAsync(progress, ct);
    }

    public void LaunchM3(AppSettings s)
    {
        var loc = M3Location.Resolve(s.PrefixDir);
        if (!loc.IsInstalled) throw new InvalidOperationException("Mod Manager is not installed yet");
        if (loc.Prefix is { } prefix) prefix.LaunchM3(loc.ExePath).Dispose();
        else Process.Start(new ProcessStartInfo(loc.ExePath) { UseShellExecute = true })?.Dispose();
    }

    private static AppConfig Config(AppSettings s) => new(s.GameDir, string.IsNullOrWhiteSpace(s.PrefixDir) ? null : s.PrefixDir);
    private static RunOptions Options(AppSettings s) => new(s.Games, SkipTextures: !s.IncludeTextures);

    public async Task<RunPlan> PlanAsync(AppSettings s, IRunHost host, CancellationToken ct)
    {
        using var services = AppServices.Create(Config(s), host);
        return await services.Orchestrator.PlanAsync(Options(s), ct);
    }

    public async Task<RunResult> RunAsync(AppSettings s, IRunHost host, CancellationToken ct)
    {
        using var services = AppServices.Create(Config(s), host);
        return await services.Orchestrator.RunAsync(Options(s), ct);
    }

    public void ResetState() { if (File.Exists(StatePath)) File.Delete(StatePath); }
}
