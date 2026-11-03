using Mele.Engine.Platform;

namespace Mele.Engine.Backup;

/// <summary>What is known about a game folder: does it look vanilla, and how does it compare with its backup. Read-only.</summary>
public sealed record GameReport(string Game, string GameFolder, string? BackupPath, VanillaReport Vanilla, CompareReport? Compare, string? NoBackupReason)
{
    public static GameReport Build(string game, string gameFolder, BackupInfo backup, CancellationToken ct = default)
    {
        var vanilla = VanillaCheck.Check(game, gameFolder);
        var compare = backup is { Valid: true, Path: { } p } ? BackupCompare.Compare(p, gameFolder, ct) : null;
        return new GameReport(game, gameFolder, backup.Valid ? backup.Path : null, vanilla, compare, backup.Valid ? null : backup.Reason);
    }

    /// <summary>True when the game looks vanilla and every backup file is in it unchanged.</summary>
    public bool Clean => Vanilla.IsVanilla && Compare is { GameContainsBackup: true };

    public IEnumerable<string> Lines()
    {
        yield return Vanilla.IsVanilla ? "Vanilla check: no mods or texture installs found" : $"Vanilla check: NOT vanilla: {Vanilla.Summary}";
        foreach (var n in Vanilla.Notes) yield return $"  note: {n}";
        if (Compare is not { } r) { yield return $"No backup to compare with ({NoBackupReason})."; yield break; }
        yield return $"Files: backup {r.BackupFiles}, game {r.GameFiles}. Languages in the backup: {string.Join(", ", r.BackupLanguages.Order())}";
        yield return $"Missing from the game: {r.Missing.Count}   different size: {r.Different.Count}";
        yield return $"Only in the game: {r.Extra.Count} (mod files {r.Count(ExtraKind.Mod)}, tools {r.Count(ExtraKind.Tool)}, localised vanilla files {r.Count(ExtraKind.Localised)} [a restore must keep these], other {r.Count(ExtraKind.Other)})";
    }
}
