using Mele.Engine.Mem;
using Mele.Engine.Nexus;
using Mele.Engine.Platform;

namespace Mele.Engine.Orchestration;

public sealed record PreflightIssue(bool Blocking, string Message);

public static class Preflight
{
    public static async Task<IReadOnlyList<PreflightIssue>> RunAsync(IReadOnlyList<string> games, RunPaths paths, IBackupLocator backups,
        Func<CancellationToken, Task<NexusUser>> validateAccount, double minFreeGb, CancellationToken ct, Func<bool>? extractorAvailable = null)
    {
        var issues = new List<PreflightIssue>();

        // Mods come as zip/7z/rar; catching a missing extractor here beats failing halfway through a 100 GB download.
        if (extractorAvailable != null && !extractorAvailable())
            issues.Add(new(true, OperatingSystem.IsWindows()
                ? "7-Zip is required to unpack mods. Install it (for example: winget install 7zip.7zip) and start again."
                : "7-Zip is required to unpack mods. Install it with your package manager (Arch: 7zip, Debian/Ubuntu: 7zip or p7zip-full) and start again."));

        try
        {
            var me = await validateAccount(ct);
            if (!me.IsPremium)
                issues.Add(new(true, "A Nexus Premium account is required: free accounts cannot get download links from the API."));
        }
        catch (NexusException e) { issues.Add(new(true, $"Nexus API key problem: {e.Message}")); }

        if (!Directory.Exists(paths.Library))
            issues.Add(new(true, $"Mod Manager's mod library was not found at {paths.Library}. Set up Mod Manager first."));

        foreach (var game in games)
        {
            var folder = Path.Combine(paths.GameRoot, "Game", $"ME{MemRunner.GameId(game)}");
            if (!Directory.Exists(folder)) issues.Add(new(true, $"{game}: game folder not found ({folder})"));
            var b = backups.Get(game);
            if (!b.Valid) issues.Add(new(true, $"{game}: no valid Mod Manager backup ({b.Reason}). Make one in Mod Manager before modding."));
        }

        foreach (var (label, dir) in new[] { ("cache", paths.CacheDir), ("mod library", paths.Library) })
        {
            var free = FreeGb(dir);
            if (free is null) continue;
            if (free < minFreeGb) issues.Add(new(true, $"Only {free:0} GB free for the {label} ({dir}); at least {minFreeGb:0} GB is needed."));
            else if (free < minFreeGb * 2) issues.Add(new(false, $"Only {free:0} GB free for the {label}; a full run may need more."));
        }
        return issues;
    }

    private static double? FreeGb(string dir)
    {
        try
        {
            var probe = dir;
            while (!Directory.Exists(probe) && Path.GetDirectoryName(probe) is { Length: > 0 } up) probe = up;
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(probe))!).AvailableFreeSpace / 1073741824.0;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }
}
