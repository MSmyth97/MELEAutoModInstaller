using System.Diagnostics;
using Mele.Engine.Platform;

namespace Mele.Engine.Backup;

public sealed class RestoreException(string message) : Exception(message);

/// <param name="Copy">Backup files to copy into the game: missing from it, or a different size.</param>
/// <param name="Delete">Files only in the game that mods or tools added.</param>
/// <param name="KeptLocalised">Vanilla files in languages the backup does not contain. Never deleted.</param>
/// <param name="KeptUnknown">Files only in the game that look like neither. Kept unless the caller opts in to deleting them.</param>
public sealed record RestorePlan(
    string Game, string BackupRoot, string GameFolder, CompareReport Report,
    IReadOnlyList<string> Copy, IReadOnlyList<string> Delete,
    IReadOnlyList<string> KeptLocalised, IReadOnlyList<string> KeptUnknown, long CopyBytes)
{
    public bool NothingToDo => Copy.Count == 0 && Delete.Count == 0;

    public IEnumerable<string> Describe()
    {
        yield return $"Copy {Copy.Count} file(s) from the backup ({CopyBytes / 1073741824.0:0.0} GB): {Report.Missing.Count} missing, {Report.Different.Count} different";
        yield return $"Delete {Delete.Count} file(s) that mods or tools added";
        yield return $"Keep {KeptLocalised.Count} vanilla file(s) in languages the backup does not contain";
        if (KeptUnknown.Count > 0) yield return $"Keep {KeptUnknown.Count} file(s) that are in neither the backup nor recognised as mod files";
    }
}

public sealed record FileProgress(string Phase, int Done, int Total, string? Path);

public sealed record RestoreResult(bool Success, int Copied, int Deleted, IReadOnlyList<string> Problems, CompareReport? After);

/// <summary>
/// Puts a game folder back to what its backup holds, without the two ways a plain mirror (<c>rsync --delete</c>, robocopy /MIR) goes wrong:
/// it never deletes vanilla files a backup lacks (languages the backup was made without), and it reports each failure instead of
/// claiming success. <see cref="Prepare"/> changes nothing, so it is the dry run.
/// </summary>
public sealed class GameRestorer(Func<bool>? gameRunning = null)
{
    /// <summary>Mod Manager keeps the user's graphics settings across a restore, and so do we.</summary>
    public static readonly IReadOnlyList<string> Preserved = ["BioGame/Config/GamerSettings.ini"];

    private readonly Func<bool> _gameRunning = gameRunning ?? DefaultGameRunning;

    public static bool DefaultGameRunning()
    {
        try { return Process.GetProcesses().Any(p => p.ProcessName.StartsWith("MassEffect", StringComparison.OrdinalIgnoreCase)); }
        catch (InvalidOperationException) { return false; }
    }

    public RestorePlan Prepare(string game, BackupInfo backup, string gameFolder, bool deleteUnknown = false, CancellationToken ct = default)
    {
        if (!backup.Valid || backup.Path is null) throw new RestoreException($"{game}: there is no valid backup to restore from ({backup.Reason}).");
        var backupRoot = Path.GetFullPath(backup.Path);
        var gameRoot = Path.GetFullPath(gameFolder);
        if (!Directory.Exists(Path.Combine(gameRoot, "BioGame")) || !Directory.Exists(Path.Combine(gameRoot, "Binaries")))
            throw new RestoreException($"{game}: {gameRoot} does not look like a game folder (no BioGame and Binaries), so nothing was touched.");
        if (Overlaps(backupRoot, gameRoot))
            throw new RestoreException($"{game}: the backup and the game folder are the same place or inside each other ({backupRoot}, {gameRoot}).");

        var report = BackupCompare.Compare(backupRoot, gameRoot, ct);
        var copy = report.Missing.Concat(report.Different.Select(d => d.Path)).Where(p => !IsPreserved(p)).ToList();
        var delete = report.Extra.Where(e => !IsPreserved(e.Path) && (e.Kind is ExtraKind.Mod or ExtraKind.Tool || (deleteUnknown && e.Kind == ExtraKind.Other))).Select(e => e.Path).ToList();
        var keptLocalised = report.Extra.Where(e => e.Kind == ExtraKind.Localised).Select(e => e.Path).ToList();
        var keptUnknown = report.Extra.Where(e => e.Kind == ExtraKind.Other && !IsPreserved(e.Path) && !deleteUnknown).Select(e => e.Path).ToList();
        var bytes = copy.Sum(p => new FileInfo(Native(backupRoot, p)).Length);
        return new RestorePlan(game, backupRoot, gameRoot, report, copy, delete, keptLocalised, keptUnknown, bytes);
    }

    public async Task<RestoreResult> RestoreAsync(RestorePlan plan, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        if (_gameRunning()) throw new RestoreException($"{plan.Game} (or another Mass Effect game) is running. Close it and try again; nothing was changed.");
        var problems = new List<string>();
        int copied = 0, deleted = 0;

        // Delete first: a mod's file must not survive just because a copy later fails.
        for (var i = 0; i < plan.Delete.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new("Removing mod files", i, plan.Delete.Count, plan.Delete[i]));
            try { DeleteFile(Native(plan.GameFolder, plan.Delete[i])); deleted++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problems.Add($"Could not delete {plan.Delete[i]}: {e.Message}"); }
        }
        problems.AddRange(RemoveEmptyFolders(plan.GameFolder, plan.BackupRoot));

        for (var i = 0; i < plan.Copy.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new("Restoring files", i, plan.Copy.Count, plan.Copy[i]));
            try { await FileCopier.CopyAsync(Native(plan.BackupRoot, plan.Copy[i]), Native(plan.GameFolder, plan.Copy[i]), ct); copied++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problems.Add($"Could not restore {plan.Copy[i]}: {e.Message}"); }
        }

        // Trust nothing: compare again, and say so if the game still differs from the backup.
        progress?.Report(new("Checking the result", 0, 1, null));
        var after = BackupCompare.Compare(plan.BackupRoot, plan.GameFolder, ct);
        var stillMissing = after.Missing.Count(p => !IsPreserved(p));
        var stillDifferent = after.Different.Count(d => !IsPreserved(d.Path));
        if (stillMissing + stillDifferent > 0)
            problems.Add($"After the restore {stillMissing} backup file(s) are still missing and {stillDifferent} still differ.");
        var planned = plan.Delete.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leftoverMods = after.Extra.Count(e => planned.Contains(e.Path));
        if (leftoverMods > 0) problems.Add($"{leftoverMods} mod file(s) could not be removed.");
        progress?.Report(new("Done", 1, 1, null));
        return new RestoreResult(problems.Count == 0, copied, deleted, problems, after);
    }

    private static bool IsPreserved(string rel) => Preserved.Contains(rel, StringComparer.OrdinalIgnoreCase);

    private static string Native(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static bool Overlaps(string a, string b)
    {
        static string Norm(string p) => Path.TrimEndingDirectorySeparator(p) + Path.DirectorySeparatorChar;
        var (x, y) = (Norm(a), Norm(b));
        return x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    /// <summary>Removes folders the restore emptied (a mod's folder, an empty BioGame/DLC) but only ones the backup does not have.</summary>
    private static List<string> RemoveEmptyFolders(string gameFolder, string backupRoot)
    {
        var problems = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(gameFolder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
        {
            if (Directory.Exists(Path.Combine(backupRoot, Path.GetRelativePath(gameFolder, dir)))) continue;
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { problems.Add($"Could not remove the empty folder {Path.GetRelativePath(gameFolder, dir)}: {e.Message}"); }
        }
        return problems;
    }
}
