using Mele.Engine.Platform;

namespace Mele.Engine.Provision;

public enum TargetStatus
{
    /// <summary>Mod Manager has this game folder as a target.</summary>
    Matches,
    /// <summary>Mod Manager has no target for this game yet.</summary>
    NotRegistered,
    /// <summary>It has a target, but that folder does not exist (a typo, or a drive letter the prefix does not map).</summary>
    Missing,
    /// <summary>It has targets and they exist, but none is this game folder.</summary>
    Different,
}

/// <param name="PathToEnter">The path to give Mod Manager for this game folder, in the form it understands.</param>
public sealed record TargetCheck(string Game, TargetStatus Status, string Message, string PathToEnter, IReadOnlyList<string> Targets)
{
    public bool Ok => Status == TargetStatus.Matches;
}

/// <summary>
/// Reads the game folders Mod Manager has registered (<c>GameTargetsLE1.txt</c> and so on: one Windows path per line, in its data folder)
/// and compares them with the game folder this app uses. A wrong or mistyped path in Mod Manager fails silently, so this is
/// where the app can say so. Read-only.
/// </summary>
public sealed class ModManagerTargets(string? dataFolder, Func<string, string?> toNative, Func<string, string?> toWindows, bool caseInsensitive)
{
    public static ModManagerTargets ForWinePrefix(WinePrefix prefix) =>
        new(Path.Combine(prefix.Root, "drive_c", "ProgramData", "ME3TweaksModManager"), prefix.ToLinuxPath, prefix.ToWindowsPath, caseInsensitive: false);

    /// <summary>Windows keeps the same files under the shared ProgramData folder (not yet confirmed on a real Windows machine).</summary>
    public static ModManagerTargets ForWindows() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ME3TweaksModManager"), p => p, p => p, caseInsensitive: true);

    public static string FileFor(string game) => $"GameTargets{game}.txt";

    /// <summary>Null when Mod Manager has not been set up here, so there is nothing to compare with.</summary>
    public TargetCheck? Check(string game, string gameFolder)
    {
        if (dataFolder == null || !Directory.Exists(dataFolder)) return null;
        var enter = toWindows(gameFolder) ?? gameFolder;
        var file = Path.Combine(dataFolder, FileFor(game));
        var targets = File.Exists(file)
            ? File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];

        if (targets.Count == 0)
            return new(game, TargetStatus.NotRegistered, $"Mod Manager does not have {game} as a target yet. In Mod Manager add the game, using this path: {enter}", enter, targets);

        var wanted = Norm(gameFolder);
        var resolved = targets.Select(t => (Target: t, Native: toNative(t) is { } n ? Norm(n) : null)).ToList();
        if (resolved.Any(r => r.Native != null && string.Equals(r.Native, wanted, Comparison)))
        {
            var many = targets.Count > 1 ? $" (it has {targets.Count} targets for {game}, so make sure this one is selected)" : "";
            return new(game, TargetStatus.Matches, $"Mod Manager's target for {game} is this game folder{many}.", enter, targets);
        }

        var existing = resolved.Where(r => r.Native != null && Directory.Exists(r.Native)).ToList();
        if (existing.Count == 0)
        {
            var shown = string.Join(", ", targets.Take(3));
            var why = resolved.All(r => r.Native == null) ? "is on a drive this prefix does not map" : "does not exist";
            return new(game, TargetStatus.Missing, $"Mod Manager's target for {game} ({shown}) {why}. Add the game again with this path: {enter}", enter, targets);
        }
        return new(game, TargetStatus.Different, $"Mod Manager's target for {game} is a different folder ({existing[0].Target}), not this game folder. To use this one, add it with this path: {enter}", enter, targets);
    }

    private StringComparison Comparison => caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
}
