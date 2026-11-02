using MonoTorrent;
using MonoTorrent.Client;

namespace Mele.Engine.Textures;

/// <summary>
/// <see cref="ITorrentFetcher"/> over MonoTorrent. Only the requested member gets a download priority.
/// A file counts as downloaded only once a "&lt;file&gt;.done" marker exists: pieces shared with neighbouring
/// files leave sparse stubs of those neighbours on disk, so the mere existence of a file proves nothing.
/// </summary>
public sealed class MonoTorrentFetcher : ITorrentFetcher
{
    public async Task<string> FetchMemberAsync(string torrentFile, string memberFileName, string saveDir,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(saveDir);
        var torrent = await Torrent.LoadAsync(torrentFile);
        var target = Path.Combine(saveDir, torrent.Name, memberFileName);
        var marker = target + ".done";
        if (File.Exists(target) && File.Exists(marker)) return target;

        var settings = new EngineSettingsBuilder { CacheDirectory = Path.Combine(saveDir, ".monotorrent") }.ToSettings();
        using var engine = new ClientEngine(settings);
        var manager = await engine.AddAsync(torrent, saveDir);

        var wanted = manager.Files.Where(f => Path.GetFileName(f.Path) == memberFileName).ToList();
        if (wanted.Count != 1) throw new InvalidOperationException($"'{memberFileName}' is not a unique member of {torrent.Name}");
        foreach (var f in manager.Files)
            await manager.SetFilePriorityAsync(f, f == wanted[0] ? Priority.Normal : Priority.DoNotDownload);

        await manager.StartAsync();
        try
        {
            while (manager.State != TorrentState.Seeding)
            {
                ct.ThrowIfCancellationRequested();
                if (manager.State == TorrentState.Error) throw new IOException($"Torrent error: {manager.Error?.Exception?.Message ?? manager.Error?.Reason.ToString()}");
                progress?.Report(manager.PartialProgress);
                await Task.Delay(1000, ct);
            }
        }
        finally { await manager.StopAsync(); }

        if (!File.Exists(target)) throw new FileNotFoundException("Torrent finished but the file is missing", target);
        File.WriteAllText(marker, "");
        progress?.Report(100);
        return target;
    }
}
