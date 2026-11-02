using Mele.Engine.Archive;
using Mele.Engine.Biq;
using Mele.Engine.Download;
using Mele.Engine.Nexus;

namespace Mele.Engine.Orchestration;

/// <param name="NexusFileId">Identifies the guide revision; a change means the guide was updated.</param>
public sealed record GuideGroup(string Game, BiqGroup Group, string FilePath, int NexusFileId);

public interface IGuideSource
{
    Task<IReadOnlyDictionary<string, GuideGroup>> FetchAsync(IEnumerable<string> games, CancellationToken ct = default);
}

/// <summary>Gets the guide's current .biq2 install groups from its Nexus page, so guide updates flow in without a new app release.</summary>
public sealed class NexusGuideSource(NexusClient nexus, Downloader downloader, IArchiveExtractor extractor, GuideConfig guide) : IGuideSource
{
    public async Task<IReadOnlyDictionary<string, GuideGroup>> FetchAsync(IEnumerable<string> games, CancellationToken ct = default)
    {
        var files = await nexus.ListFilesAsync(guide.GameDomain, guide.GuideModId, ct);
        var result = new Dictionary<string, GuideGroup>();
        foreach (var game in games)
        {
            var wanted = guide.GroupNameFor(game);
            // Current files only: the page also lists archived/old versions of every group.
            var file = files.Where(f => f.Category.Equals("MAIN", StringComparison.OrdinalIgnoreCase)
                                        && f.Name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(f => f.FileId).FirstOrDefault()
                       ?? throw new NexusException($"The guide has no current file named '{wanted}'");
            var (path, _) = await downloader.FetchAsync(guide.GameDomain, guide.GuideModId, file.FileId, ct);
            var biq = await FindBiqAsync(path, ct);
            var group = BiqGroup.Load(biq).WithLinks(guide.LinkOverrides);
            if (!string.Equals(group.Game, game, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"'{file.Name}' is a {group.Game} group, expected {game}");
            result[game] = new GuideGroup(game, group, biq, file.FileId);
        }
        return result;
    }

    /// <summary>The group file may be the .biq2 itself or an archive holding it; archives are unpacked next to the download.</summary>
    private async Task<string> FindBiqAsync(string downloaded, CancellationToken ct)
    {
        if (downloaded.EndsWith(".biq2", StringComparison.OrdinalIgnoreCase)) return downloaded;
        var dir = Path.Combine(Path.GetDirectoryName(downloaded)!, "unpacked");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        await extractor.ExtractAsync(downloaded, dir, ct);
        return Directory.EnumerateFiles(dir, "*.biq2", SearchOption.AllDirectories).FirstOrDefault()
               ?? throw new InvalidDataException($"No .biq2 file inside {Path.GetFileName(downloaded)}");
    }
}
