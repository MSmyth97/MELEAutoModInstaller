using Mele.Engine.Backup;

namespace Mele.Tests;

public class BackupCompareTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private string Root(string name) => Directory.CreateDirectory(System.IO.Path.Combine(_tmp.Path, name)).FullName;

    private static void Put(string root, string rel, int size = 1)
    {
        var path = System.IO.Path.Combine(root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    [Theory]
    [InlineData("BioGame/CookedPCConsole/BIOA_UNC52_00survey_DSG_LOC_DE.pcc", "DE")]
    [InlineData("BioGame/Content/Packages/ISACT/wrex_plpc.isb", "PLPC")]
    [InlineData("BioGame/CookedPCConsole/Startup_INT.pcc", "INT")]
    [InlineData("BioGame/CookedPCConsole/BIOA_ICE20_01A_DSG.pcc", null)]       // "DSG" is not a language
    [InlineData("BioGame/Content/Packages/ISACT/arcelia.isb", null)]            // no underscore
    [InlineData("BioGame/CookedPCConsole/Name_Of_Thing_ra.pcc", "RA")]
    public void Language_comes_from_the_last_underscore_segment(string path, string? expected) =>
        Assert.Equal(expected, ModFiles.LanguageOf(path));

    [Fact]
    public void Game_root_is_game_me_n_under_the_game_folder()
    {
        Assert.Equal(System.IO.Path.Combine("/g", "Game", "ME2"), GameFolders.Root("/g", "LE2"));
        Assert.Throws<ArgumentException>(() => GameFolders.Root("/g", "ME2"));
    }

    [Fact]
    public void An_untouched_folder_is_vanilla_even_if_it_is_empty()
    {
        var game = Root("game");
        Assert.True(VanillaCheck.Check("LE1", game).IsVanilla);
        Put(game, "BioGame/CookedPCConsole/Textures.tfc");
        Put(game, "BioGame/DLC/DLC_UNC/x.pcc");                 // official DLC is not a mod
        Assert.True(VanillaCheck.Check("LE1", game).IsVanilla);
    }

    [Fact]
    public void Mod_folders_and_texture_files_make_a_game_not_vanilla()
    {
        var game = Root("game");
        Put(game, "BioGame/DLC/DLC_MOD_ALOT/CookedPCConsole/a.pcc");
        Put(game, "BioGame/DLC/DLC_MOD_Vegas/x.pcc");
        Put(game, "BioGame/CookedPCConsole/TexturesMEM0000.tfc");
        Put(game, "BioGame/CookedPCConsole/Textures_DLC_MOD_AVC.tfc");
        Put(game, "Binaries/Win64/ASI/AutoTOCLE.asi");
        Put(game, "Binaries/Win64/bink2w64_original.dll");

        var r = VanillaCheck.Check("LE1", game);
        Assert.False(r.IsVanilla);
        Assert.Equal(2, r.Problems.Count);
        Assert.Contains("2 mod folder(s)", r.Summary);
        Assert.Contains("2 texture file(s)", r.Summary);
        Assert.Equal(2, r.Notes.Count);                         // ASI + Bink proxy are only notes
    }

    [Fact]
    public void Compare_reports_missing_different_and_classified_extras()
    {
        var backup = Root("backup"); var game = Root("game");
        foreach (var root in new[] { backup, game })
        {
            Put(root, "BioGame/CookedPCConsole/Startup_INT.pcc", 10);
            Put(root, "Binaries/Win64/MassEffect1.exe", 5);
        }
        Put(backup, "cmm_vanilla", 0);                                           // the marker is never compared
        Put(backup, "BioGame/CookedPCConsole/gone.pcc", 3);                      // missing from the game
        Put(backup, "BioGame/CookedPCConsole/changed.pcc", 3);
        Put(game, "BioGame/CookedPCConsole/changed.pcc", 9);                     // different size
        Put(game, "BioGame/CookedPCConsole/Startup_DE.pcc", 4);                  // vanilla German file the English-only backup lacks
        Put(game, "BioGame/Content/Packages/ISACT/wrex_fr.isb", 4);
        Put(game, "BioGame/DLC/DLC_MOD_Vegas/x.pcc", 4);                         // mod
        Put(game, "BioGame/DLC/DLC_MOD_Vegas/x_DE.pcc", 4);                      // a mod's own German file is still a mod file
        Put(game, "BioGame/CookedPCConsole/TexturesMEM0000.tfc", 4);
        Put(game, "Binaries/Win64/ASI/loader.asi", 4);

        var r = BackupCompare.Compare(backup, game);

        Assert.Equal(["BioGame/CookedPCConsole/gone.pcc"], r.Missing);
        var d = Assert.Single(r.Different);
        Assert.Equal(("BioGame/CookedPCConsole/changed.pcc", 3L, 9L), (d.Path, d.BackupSize, d.GameSize));
        Assert.Equal(["INT"], r.BackupLanguages);
        Assert.False(r.GameContainsBackup);
        Assert.Equal(2, r.Count(ExtraKind.Localised));
        Assert.Equal(3, r.Count(ExtraKind.Mod));
        Assert.Equal(1, r.Count(ExtraKind.Tool));
        Assert.Contains(r.Protected, e => e.Path == "BioGame/Content/Packages/ISACT/wrex_fr.isb");
        Assert.DoesNotContain(r.Protected, e => e.Path.Contains("DLC_MOD_"));
    }

    [Fact]
    public void A_language_the_backup_contains_is_not_protected()
    {
        var backup = Root("backup"); var game = Root("game");
        Put(backup, "BioGame/CookedPCConsole/Startup_INT.pcc");
        Put(backup, "BioGame/CookedPCConsole/Startup_DE.pcc");                   // the user kept German in the backup
        Put(game, "BioGame/CookedPCConsole/Startup_INT.pcc");
        Put(game, "BioGame/CookedPCConsole/Startup_DE.pcc");
        Put(game, "BioGame/CookedPCConsole/Extra_DE.pcc");                       // not in a backup that has German: not vanilla

        var r = BackupCompare.Compare(backup, game);
        Assert.True(r.GameContainsBackup);
        var extra = Assert.Single(r.Extra);
        Assert.Equal(ExtraKind.Other, extra.Kind);
    }

    [Fact]
    public void A_restored_game_matches_the_backup_exactly()
    {
        var backup = Root("backup"); var game = Root("game");
        foreach (var root in new[] { backup, game }) Put(root, "BioGame/CookedPCConsole/Startup_INT.pcc", 7);
        var r = BackupCompare.Compare(backup, game);
        Assert.True(r.GameContainsBackup);
        Assert.Empty(r.Extra);
        Assert.Equal((1, 1), (r.BackupFiles, r.GameFiles));
    }
}
