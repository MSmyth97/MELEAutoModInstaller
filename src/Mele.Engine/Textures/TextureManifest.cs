using System.Text.Json;

namespace Mele.Engine.Textures;

public enum TextureKind { M3to, Mem }

/// <param name="Select">For .mem mods: the only files to install (the guide lists them). Null = every .mem in the archive.</param>
public sealed record TextureItem(string Name, TextureKind Kind, int? ModId, int? FileId, string? TorrentMember, IReadOnlyList<string>? Select);

public sealed record TorrentRef(int ModId, int FileId);

/// <summary>The guide's texture section (textures.json): install order = list order.</summary>
public sealed class TextureManifest
{
    public required string GameDomain { get; init; }
    public required TorrentRef Torrent { get; init; }
    private readonly Dictionary<string, IReadOnlyList<TextureItem>> _games = [];

    public IReadOnlyList<TextureItem> Items(string game) =>
        _games.TryGetValue(game, out var items) ? items : throw new KeyNotFoundException($"No texture list for {game}");

    public static TextureManifest LoadDefault() => Parse(Orchestration.EmbeddedData.Read("textures.json"));

    public static TextureManifest Load(string path) => Parse(File.ReadAllText(path));

    public static TextureManifest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var t = root.GetProperty("torrent");
        var m = new TextureManifest
        {
            GameDomain = root.GetProperty("game_domain").GetString()!,
            Torrent = new TorrentRef(t.GetProperty("mod_id").GetInt32(), t.GetProperty("file_id").GetInt32()),
        };
        foreach (var game in new[] { "LE1", "LE2", "LE3" })
        {
            if (!root.TryGetProperty(game, out var arr)) continue;
            m._games[game] = arr.EnumerateArray().Select(Item).ToList();
        }
        return m;
    }

    private static TextureItem Item(JsonElement e)
    {
        var kind = e.GetProperty("type").GetString() switch
        {
            "m3to" => TextureKind.M3to,
            "mem" => TextureKind.Mem,
            var other => throw new InvalidDataException($"Unknown texture type '{other}'"),
        };
        return new TextureItem(
            e.GetProperty("name").GetString()!, kind,
            e.TryGetProperty("mod_id", out var mid) ? mid.GetInt32() : null,
            e.TryGetProperty("file_id", out var fid) ? fid.GetInt32() : null,
            e.TryGetProperty("torrent_member", out var tm) ? tm.GetString() : null,
            e.TryGetProperty("select", out var sel) ? sel.EnumerateArray().Select(x => x.GetString()!).ToList() : null);
    }
}
