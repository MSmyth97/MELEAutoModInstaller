using System.Text.Json;

namespace Mele.Engine.Orchestration;

public sealed class GameState
{
    /// <summary>Fingerprint of the guide's mod list when M3 was last confirmed; a changed guide invalidates that confirmation.</summary>
    public string? ContentSignature { get; set; }
    public bool TexturesPrepared { get; set; }
    public bool M3Confirmed { get; set; }
    public bool TexturesInstalled { get; set; }
}

/// <summary>What has already been done, so a restart does not redownload or redo steps (ISL alone is 15 GB).</summary>
public sealed class RunState
{
    public Dictionary<string, GameState> Games { get; set; } = [];
    public GameState For(string game) => Games.TryGetValue(game, out var g) ? g : Games[game] = new GameState();

    public static RunState Load(string path)
    {
        if (!File.Exists(path)) return new RunState();
        try { return JsonSerializer.Deserialize<RunState>(File.ReadAllText(path)) ?? new RunState(); }
        catch (JsonException) { return new RunState(); } // a corrupt state file must never block a run; steps are idempotent
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true); // atomic: a crash mid-write cannot leave half a file
    }
}
