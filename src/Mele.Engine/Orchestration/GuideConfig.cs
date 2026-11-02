using System.Text.Json;

namespace Mele.Engine.Orchestration;

/// <summary>The guide's identity and the hand-maintained gaps in its group files (guide.json).</summary>
public sealed class GuideConfig
{
    public required string GameDomain { get; init; }
    public required int GuideModId { get; init; }
    public required string GroupNamePattern { get; init; }
    public required IReadOnlyDictionary<string, string> LinkOverrides { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> ManualSteps { get; init; }

    public string GroupNameFor(string game) => GroupNamePattern.Replace("{game}", game);

    public static GuideConfig LoadDefault() => Parse(EmbeddedData.Read("guide.json"));

    public static GuideConfig Load(string path) => Parse(File.ReadAllText(path));

    public static GuideConfig Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new GuideConfig
        {
            GameDomain = r.GetProperty("game_domain").GetString()!,
            GuideModId = r.GetProperty("guide_mod_id").GetInt32(),
            GroupNamePattern = r.GetProperty("group_name_pattern").GetString()!,
            LinkOverrides = r.TryGetProperty("link_overrides", out var lo)
                ? lo.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!) : [],
            ManualSteps = r.TryGetProperty("manual_steps", out var ms)
                ? ms.EnumerateObject().ToDictionary(p => p.Name, p => (IReadOnlyList<string>)p.Value.EnumerateArray().Select(x => x.GetString()!).ToList()) : [],
        };
    }
}
