using Mele.Engine.Mem;
using Mele.Engine.Platform;

namespace Mele.Engine.Backup;

public sealed class BackupException(string message) : Exception(message);

public sealed record BackupPlan(string Game, string GameFolder, string Destination, int Files, long Bytes);

/// <param name="Success">The backup is complete, verified against the game and carries the marker Mod Manager looks for.</param>
/// <param name="Registered">Mod Manager has been told where it is. False means the user must link it in Mod Manager.</param>
public sealed record BackupResult(bool Success, bool Registered, IReadOnlyList<string> Problems);

/// <summary>
/// Makes a Mod Manager-compatible vanilla backup: a full copy of the game folder, verified, with the empty <c>cmm_vanilla</c> marker written
/// last (so an interrupted copy never looks like a valid backup), then registered where Mod Manager reads it. Mod Manager's own backup
/// leaves out files for languages the user did not pick; this one keeps everything, which Mod Manager accepts too.
/// </summary>
public sealed class BackupCreator(IBackupRegistry registry, Func<bool>? gameRunning = null)
{
    private readonly Func<bool> _gameRunning = gameRunning ?? GameRestorer.DefaultGameRunning;

    /// <summary>A sibling of the game folder: <c>…/Games/ME1 Backup</c> next to <c>…/Games/Mass Effect Legendary Edition</c>.</summary>
    public static string DefaultDestination(string gameDir, string game) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDir))) ?? gameDir, $"ME{MemRunner.GameId(game)} Backup");

    public BackupPlan Prepare(string game, string gameFolder, string destination, BackupInfo existing, bool replaceExisting = false, CancellationToken ct = default)
    {
        var folder = Path.GetFullPath(gameFolder);
        var dest = Path.GetFullPath(destination);
        var exe = Path.Combine(folder, "Binaries", "Win64", $"MassEffect{MemRunner.GameId(game)}.exe");
        if (!Directory.Exists(Path.Combine(folder, "BioGame")) || !File.Exists(exe))
            throw new BackupException($"{game}: {folder} does not look like a game folder (no BioGame, or no {Path.GetFileName(exe)}).");

        var vanilla = VanillaCheck.Check(game, folder);
        if (!vanilla.IsVanilla)
            throw new BackupException($"{game}: the game is not vanilla ({vanilla.Summary}). A backup of a modded game would bring the mods back when restored. Restore the game from a real backup first.");

        if (existing.Valid && !replaceExisting)
            throw new BackupException($"{game}: Mod Manager already has a backup at {existing.Path}. Use it, or choose to replace it.");

        static string Norm(string p) => Path.TrimEndingDirectorySeparator(p) + Path.DirectorySeparatorChar;
        if (Norm(dest).StartsWith(Norm(folder), StringComparison.OrdinalIgnoreCase) || Norm(folder).StartsWith(Norm(dest), StringComparison.OrdinalIgnoreCase))
            throw new BackupException($"{game}: the backup folder and the game folder must be separate places ({dest}).");
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
            throw new BackupException($"{game}: the backup folder {dest} is not empty. Choose an empty or new folder.");

        var files = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        ct.ThrowIfCancellationRequested();
        var bytes = files.Sum(f => f.Length);
        if (DiskSpace.FreeGb(dest) is { } free && free * 1073741824.0 < bytes * 1.1)
            throw new BackupException($"{game}: the backup needs about {bytes * 1.1 / 1073741824.0:0} GB but only {free:0} GB is free where {dest} is.");
        return new BackupPlan(game, folder, dest, files.Count, bytes);
    }

    public async Task<BackupResult> CreateAsync(BackupPlan plan, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        if (_gameRunning()) throw new BackupException($"{plan.Game} (or another Mass Effect game) is running. Close it and try again; nothing was copied.");
        var problems = new List<string>();
        Directory.CreateDirectory(plan.Destination);
        foreach (var dir in Directory.EnumerateDirectories(plan.GameFolder, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(plan.Destination, Path.GetRelativePath(plan.GameFolder, dir)));

        var files = new DirectoryInfo(plan.GameFolder).EnumerateFiles("*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(plan.GameFolder, f.FullName)).ToList();
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new("Copying the game", i, files.Count, files[i]));
            try { await FileCopier.CopyAsync(Path.Combine(plan.GameFolder, files[i]), Path.Combine(plan.Destination, files[i]), ct); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { problems.Add($"Could not copy {files[i]}: {e.Message}"); }
        }
        if (problems.Count > 0)
        {
            problems.Add("The backup is incomplete, so it was not marked as valid. Delete the folder and try again.");
            return new BackupResult(false, false, problems);
        }

        progress?.Report(new("Checking the copy", 0, 1, null));
        var check = BackupCompare.Compare(plan.Destination, plan.GameFolder, ct);
        if (!check.GameContainsBackup || check.Extra.Count > 0)
        {
            problems.Add($"The copy does not match the game ({check.Missing.Count} missing, {check.Different.Count} different, {check.Extra.Count} extra), so it was not marked as valid.");
            return new BackupResult(false, false, problems);
        }

        File.Create(Path.Combine(plan.Destination, BackupRules.MarkerFile)).Dispose();   // last: only a finished, verified copy is valid
        var registered = false;
        try
        {
            progress?.Report(new("Telling Mod Manager", 0, 1, null));
            await registry.SetAsync(plan.Game, plan.Destination, ct);
            registered = true;
        }
        catch (Exception e) when (e is Provision.ProvisionException or IOException or UnauthorizedAccessException)
        {
            problems.Add($"The backup is complete but Mod Manager does not know about it yet ({e.Message}). In Mod Manager, choose the backup option to link an existing backup and pick {plan.Destination}.");
        }
        progress?.Report(new("Done", 1, 1, null));
        return new BackupResult(true, registered, problems);
    }
}
