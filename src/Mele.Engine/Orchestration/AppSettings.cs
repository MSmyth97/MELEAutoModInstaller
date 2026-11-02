using System.Text.Json;
using Mele.Engine.Platform;

namespace Mele.Engine.Orchestration;

/// <summary>The user's choices. The Nexus key is deliberately not here (see <see cref="ApiKeyStore"/>).</summary>
public sealed class AppSettings
{
    public string GameDir { get; set; } = "";
    /// <summary>Linux: the Wine/Proton prefix Mod Manager runs in. Empty = the app's own.</summary>
    public string PrefixDir { get; set; } = "";
    public bool Le1 { get; set; } = true;
    public bool Le2 { get; set; } = true;
    public bool Le3 { get; set; } = true;
    public bool IncludeTextures { get; set; } = true;

    public IReadOnlyList<string> Games => new[] { Le1 ? "LE1" : null, Le2 ? "LE2" : null, Le3 ? "LE3" : null }.OfType<string>().ToList();

    public static string DefaultFile => Path.Combine(AppPaths.ConfigDir, "settings.json");

    public static AppSettings Load(string? file = null)
    {
        file ??= DefaultFile;
        try { return File.Exists(file) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    public void Save(string? file = null)
    {
        file ??= DefaultFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
