using Mele.Engine.Textures;

namespace Mele.Engine.Orchestration;

public sealed record TextureStageResult(IReadOnlyList<TextureResult> Items, string? BatchGroup, string? Mfl)
{
    public bool Success => Items.All(i => i.Status != TextureStatus.Failed) && BatchGroup != null && Mfl != null;
}

public interface ITextureStage
{
    /// <summary>Fetches every texture item for a game and, only if all succeeded, writes the M3 batch group and the ordered MEM list.</summary>
    Task<TextureStageResult> PrepareAsync(string game, string library, string memRoot, string mflPath, bool keepArchives,
        Action<TextureResult>? onItem, IProgress<double>? progress, CancellationToken ct);
}

public sealed class TextureStage(TexturePipeline pipeline, TextureManifest manifest, string cacheDir) : ITextureStage
{
    public async Task<TextureStageResult> PrepareAsync(string game, string library, string memRoot, string mflPath, bool keepArchives,
        Action<TextureResult>? onItem, IProgress<double>? progress, CancellationToken ct)
    {
        var results = new List<TextureResult>();
        var items = manifest.Items(game);
        for (int i = 0; i < items.Count; i++)
        {
            var r = await pipeline.ProcessAsync(game, i + 1, items[i], library, memRoot, Path.Combine(cacheDir, "torrent"), keepArchives, progress, ct);
            results.Add(r);
            onItem?.Invoke(r);
            if (r.Status == TextureStatus.Failed) return new TextureStageResult(results, null, null); // later items may depend on order; stop
        }
        var (batch, mfl) = pipeline.WriteOutputs(game, library, memRoot, mflPath);
        return new TextureStageResult(results, batch, mfl);
    }
}
