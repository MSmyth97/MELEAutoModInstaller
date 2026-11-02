using System.IO.Compression;
using Mele.Engine.Archive;
using Mele.Engine.Biq;
using Mele.Engine.Download;
using Mele.Engine.Nexus;
using Mele.Engine.Stage;

namespace Mele.Engine.Textures;

public enum TextureStatus { Staged, MemReady, Failed }

public sealed record TextureResult(string Game, int Index, string Name, TextureStatus Status, string Note);

/// <summary>
/// Fetches one texture item per call. M3TO items (ALOT, ISL) are staged into the M3 library like content mods;
/// .mem items are extracted (with the guide's file selection) to <c>memRoot/&lt;game&gt;/NN name</c> for MEM to install.
/// </summary>
public sealed class TexturePipeline(Downloader downloader, ITorrentFetcher torrents, Stager stager, IArchiveExtractor extractor, TextureManifest manifest)
{
    public const string TorrentFileName = "ALOT_ISL_M3TO.torrent";

    public async Task<TextureResult> ProcessAsync(string game, int index, TextureItem item, string library, string memRoot,
        string torrentDir, bool keepArchives, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            string archive;
            string how;
            Action cleanup;
            if (item.TorrentMember != null)
            {
                var torrentFile = await EnsureTorrentFileAsync(torrentDir, ct);
                archive = await torrents.FetchMemberAsync(torrentFile, item.TorrentMember, torrentDir, progress, ct);
                how = "torrent";
                cleanup = () => { File.Delete(archive); File.Delete(archive + ".done"); };
            }
            else
            {
                var fileId = item.FileId ?? throw new NexusException($"{item.Name}: manifest has neither file_id nor torrent_member");
                var (path, result) = await downloader.FetchAsync(manifest.GameDomain, item.ModId ?? manifest.Torrent.ModId, fileId, ct);
                archive = path;
                how = result.ToString().ToLowerInvariant();
                cleanup = () => downloader.Evict(fileId);
            }

            TextureResult done;
            if (item.Kind == TextureKind.M3to)
            {
                var entry = new ModEntry(item.Name, manifest.GameDomain, item.ModId, item.FileId, $"{game}/{item.Name}/moddesc.ini", "", 0, null);
                var status = await stager.StageAsync(archive, library, entry, ct);
                done = new TextureResult(game, index, item.Name, TextureStatus.Staged, $"{how}; {status}");
            }
            else
            {
                var dest = MemFiles.ItemDir(memRoot, game, index, item);
                var files = await MemFiles.ExtractAsync(extractor, archive, dest, item.Select, ct);
                done = new TextureResult(game, index, item.Name, TextureStatus.MemReady, $"{how}; {files.Count} file(s)");
            }
            if (!keepArchives) cleanup();
            return done;
        }
        catch (Exception e) when (e is NexusException or StageException or ArchiveException or IOException or HttpRequestException or InvalidOperationException)
        {
            return new TextureResult(game, index, item.Name, TextureStatus.Failed, e.Message);
        }
    }

    /// <summary>Gets the ALOT/ISL M3TO torrent file (shipped zipped as a Nexus file).</summary>
    private async Task<string> EnsureTorrentFileAsync(string torrentDir, CancellationToken ct)
    {
        var target = Path.Combine(torrentDir, TorrentFileName);
        if (File.Exists(target)) return target;
        Directory.CreateDirectory(torrentDir);
        var (zip, _) = await downloader.FetchAsync(manifest.GameDomain, manifest.Torrent.ModId, manifest.Torrent.FileId, ct);
        using var z = ZipFile.OpenRead(zip);
        var entry = z.Entries.FirstOrDefault(e => e.Name == TorrentFileName) ?? throw new IOException($"{TorrentFileName} not found in {Path.GetFileName(zip)}");
        entry.ExtractToFile(target, overwrite: true);
        return target;
    }

    /// <summary>After every item for a game succeeded: writes the M3 batch group for ALOT/ISL and the ordered MEM file list.</summary>
    public (string BatchGroup, string Mfl) WriteOutputs(string game, string library, string memRoot, string mflPath, Func<string, string>? mapPath = null)
    {
        var batch = M3toBatchWriter.Write(manifest, game, library, Path.Combine(library, "BatchModQueues"));
        MemFiles.WriteMfl(mflPath, MemFiles.OrderedFiles(manifest, game, memRoot), mapPath);
        return (batch, mflPath);
    }
}
