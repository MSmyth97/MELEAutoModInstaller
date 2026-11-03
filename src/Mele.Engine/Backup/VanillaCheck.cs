namespace Mele.Engine.Backup;

public sealed record VanillaReport(IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    public bool IsVanilla => Problems.Count == 0;
    public string Summary => string.Join("; ", Problems);
}

/// <summary>
/// A cheap check that a game folder has no mods or texture installs. It looks only for what mods leave behind, so it can run in
/// seconds on any machine and never needs a database of vanilla files. A clean result does not prove a game is untouched.
/// </summary>
public static class VanillaCheck
{
    public static VanillaReport Check(string game, string gameFolder)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        var bioGame = Path.Combine(gameFolder, "BioGame");

        var dlc = Path.Combine(bioGame, "DLC");
        if (Directory.Exists(dlc))
        {
            var mods = Directory.EnumerateDirectories(dlc).Select(Path.GetFileName).Where(n => n != null && ModFiles.IsModFolder(n)).Order().ToList();
            if (mods.Count > 0)
                problems.Add($"{mods.Count} mod folder(s) in BioGame/DLC (for example {string.Join(", ", mods.Take(3))})");
        }

        if (Directory.Exists(bioGame))
        {
            var textures = Directory.EnumerateFiles(bioGame, "*.tfc", SearchOption.AllDirectories).Count(ModFiles.IsTextureModFile);
            if (textures > 0) problems.Add($"{textures} texture file(s) written by Mass Effect Modder or texture mods");
        }

        var bin = Path.Combine(gameFolder, "Binaries", "Win64");
        if (Directory.Exists(Path.Combine(bin, "ASI")) && Directory.EnumerateFiles(Path.Combine(bin, "ASI"), "*.asi").Any())
            notes.Add("ASI mods are installed");
        if (File.Exists(Path.Combine(bin, "bink2w64_original.dll"))) notes.Add("the Bink video proxy is installed");
        return new VanillaReport(problems, notes);
    }
}
