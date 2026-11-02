namespace Mele.Engine.Textures;

/// <summary>Downloads a single file out of a multi-file torrent (the ISL M3TO archives exist only as a torrent).</summary>
public interface ITorrentFetcher
{
    /// <returns>Path of the downloaded member. Only a verified, complete file is ever returned.</returns>
    Task<string> FetchMemberAsync(string torrentFile, string memberFileName, string saveDir,
        IProgress<double>? progress = null, CancellationToken ct = default);
}
