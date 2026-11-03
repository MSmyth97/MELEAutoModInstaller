namespace Mele.Engine.Backup;

/// <summary>Recognises files that mods and Mass Effect Modder add to a game, and the language suffix of localised vanilla files.</summary>
public static class ModFiles
{
    /// <summary>
    /// Suffixes of localised files (<c>Name_DE.pcc</c>, <c>wrex_fr.isb</c>). Mod Manager's backup skips the ones for languages the user did not
    /// select, so a game can hold vanilla files that its backup does not.
    /// </summary>
    public static readonly IReadOnlySet<string> Languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "INT", "DE", "ES", "FR", "IT", "PLPC", "RA", "JA", "PL", "RU", "FE", "GE", "IE", "CS", "CZ", "HU", "KO", "ZH" };

    public static string? LanguageOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var i = name.LastIndexOf('_');
        if (i < 0) return null;
        var suffix = name[(i + 1)..];
        return Languages.Contains(suffix) ? suffix.ToUpperInvariant() : null;
    }

    public static bool IsModFolder(string name) => name.StartsWith("DLC_MOD_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Texture files written by Mass Effect Modder or by a texture mod's DLC.</summary>
    public static bool IsTextureModFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".tfc", StringComparison.OrdinalIgnoreCase)
               && (name.StartsWith("TexturesMEM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Textures_DLC_MOD_", StringComparison.OrdinalIgnoreCase));
    }
}
