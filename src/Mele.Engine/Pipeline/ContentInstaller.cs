using Mele.Engine.Archive;
using Mele.Engine.Biq;
using Mele.Engine.Download;
using Mele.Engine.Nexus;
using Mele.Engine.Stage;

namespace Mele.Engine.Pipeline;

public enum ContentStatus { AlreadyStaged, Installed, InstalledVersionDiffers, NeedsManualDownload, WouldInstall, Failed }

public sealed record ContentResult(ModEntry Mod, ContentStatus Status, string Note);

/// <summary>Downloads and stages every mod in a group into the M3 mod library, skipping what is already there.</summary>
public sealed class ContentInstaller(IModFileSource files, Stager stager)
{
    /// <summary>Read-only: what would an install do right now? (the "parity" view of a library)</summary>
    public static IReadOnlyList<ContentResult> Survey(BiqGroup group, string library) =>
        group.Mods.Select(m => Stager.IsStaged(library, m) ? new ContentResult(m, ContentStatus.AlreadyStaged, "moddesc.ini hash matches")
            : !m.Downloadable ? new ContentResult(m, ContentStatus.NeedsManualDownload, "no Nexus link in group file")
            : new ContentResult(m, ContentStatus.WouldInstall, "not staged")).ToList();

    public async Task<IReadOnlyList<ContentResult>> InstallAsync(BiqGroup group, string library, int jobs = 3, bool keepArchives = false,
        Func<ModEntry, bool>? filter = null, Action<ContentResult>? onResult = null, CancellationToken ct = default)
    {
        var results = new List<ContentResult>();
        var mods = group.Mods.Where(m => filter?.Invoke(m) ?? true).ToList();
        await Parallel.ForEachAsync(mods, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs), CancellationToken = ct }, async (m, token) =>
        {
            var r = await InstallOneAsync(m, library, keepArchives, token);
            lock (results) results.Add(r);
            onResult?.Invoke(r);
        });
        return results;
    }

    private async Task<ContentResult> InstallOneAsync(ModEntry m, string library, bool keepArchives, CancellationToken ct)
    {
        if (Stager.IsStaged(library, m)) return new(m, ContentStatus.AlreadyStaged, "moddesc.ini hash matches");
        if (!m.Downloadable) return new(m, ContentStatus.NeedsManualDownload, "no Nexus link in group file");
        try
        {
            var (archive, how) = await files.FetchAsync(m, ct);
            var status = await stager.StageAsync(archive, library, m, ct);
            if (!keepArchives) files.Evict(m.FileId!.Value);
            return status == StageStatus.Ok
                ? new(m, ContentStatus.Installed, how.ToString().ToLowerInvariant())
                : new(m, ContentStatus.InstalledVersionDiffers, $"{how.ToString().ToLowerInvariant()}; archive has a different moddesc.ini than the group file (mod updated?)");
        }
        catch (Exception e) when (e is NexusException or StageException or ArchiveException or IOException or HttpRequestException)
        {
            return new(m, ContentStatus.Failed, e.Message);
        }
    }
}
