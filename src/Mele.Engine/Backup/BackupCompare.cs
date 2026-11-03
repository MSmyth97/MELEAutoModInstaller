namespace Mele.Engine.Backup;

public enum ExtraKind
{
    /// <summary>Added by a mod or by Mass Effect Modder.</summary>
    Mod,
    /// <summary>Under Binaries: tools such as the ASI loader, logs and runtime DLLs.</summary>
    Tool,
    /// <summary>A localised file whose language the backup does not contain. Almost certainly vanilla: a restore must not delete it.</summary>
    Localised,
    Other,
}

public sealed record SizeDiff(string Path, long BackupSize, long GameSize);
public sealed record ExtraFile(string Path, ExtraKind Kind);

public sealed record CompareReport(
    IReadOnlyList<string> Missing,
    IReadOnlyList<SizeDiff> Different,
    IReadOnlyList<ExtraFile> Extra,
    IReadOnlySet<string> BackupLanguages,
    int BackupFiles,
    int GameFiles)
{
    /// <summary>Every file in the backup is in the game with the same size (extras are allowed).</summary>
    public bool GameContainsBackup => Missing.Count == 0 && Different.Count == 0;
    public IEnumerable<ExtraFile> Protected => Extra.Where(e => e.Kind == ExtraKind.Localised);
    public int Count(ExtraKind kind) => Extra.Count(e => e.Kind == kind);
}

/// <summary>
/// Compares a game folder with a Mod Manager backup by relative path and size (the same level of checking Mod Manager does),
/// and classifies files that are only in the game, so a restore can tell mod files from vanilla files the backup never had.
/// </summary>
public static class BackupCompare
{
    private const string Marker = "cmm_vanilla";

    public static CompareReport Compare(string backupRoot, string gameFolder, CancellationToken ct = default)
    {
        var backup = Index(backupRoot, ct);
        var game = Index(gameFolder, ct);
        var languages = new HashSet<string>(backup.Keys.Select(ModFiles.LanguageOf).Where(l => l != null).Select(l => l!), StringComparer.OrdinalIgnoreCase);

        var missing = new List<string>();
        var different = new List<SizeDiff>();
        foreach (var (path, size) in backup)
        {
            if (!game.TryGetValue(path, out var gs)) missing.Add(path);
            else if (gs != size) different.Add(new SizeDiff(path, size, gs));
        }
        var extra = game.Keys.Where(p => !backup.ContainsKey(p)).Order(StringComparer.Ordinal)
            .Select(p => new ExtraFile(p, Classify(p, languages))).ToList();
        return new CompareReport(missing.Order(StringComparer.Ordinal).ToList(), different, extra, languages, backup.Count, game.Count);
    }

    public static ExtraKind Classify(string relativePath, IReadOnlySet<string> backupLanguages)
    {
        var p = relativePath.Replace('\\', '/');
        if (p.StartsWith("BioGame/DLC/", StringComparison.OrdinalIgnoreCase) && p.Split('/') is [_, _, var folder, ..] && ModFiles.IsModFolder(folder))
            return ExtraKind.Mod;
        if (ModFiles.IsTextureModFile(p)) return ExtraKind.Mod;
        if (ModFiles.LanguageOf(p) is { } lang && !backupLanguages.Contains(lang)) return ExtraKind.Localised;
        return p.StartsWith("Binaries/", StringComparison.OrdinalIgnoreCase) ? ExtraKind.Tool : ExtraKind.Other;
    }

    private static Dictionary<string, long> Index(string root, CancellationToken ct)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
            if (rel.Equals(Marker, StringComparison.OrdinalIgnoreCase)) continue;
            map[rel] = file.Length;
        }
        return map;
    }
}
