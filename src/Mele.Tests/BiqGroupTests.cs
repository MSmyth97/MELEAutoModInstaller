using Mele.Engine.Biq;

namespace Mele.Tests;

public class BiqGroupTests
{
    [Theory]
    [InlineData("LE1 Mod Collection (2026) - 201.biq2", "LE1", 63, 63)]
    [InlineData("LE2 Mod Collection (2026) - 201.biq2", "LE2", 82, 81)]
    [InlineData("LE3 Mod Collection (2026) - 201.biq2", "LE3", 84, 83)]
    public void Loads_guide_groups(string file, string game, int total, int downloadable)
    {
        var g = BiqGroup.Load(Path.Combine(Repo.Samples, file));
        Assert.Equal(game, g.Game);
        Assert.Equal(total, g.Mods.Count);
        Assert.Equal(downloadable, g.Mods.Count(m => m.Downloadable));
    }

    [Fact]
    public void Parses_link_and_hash()
    {
        var m = BiqGroup.Load(Path.Combine(Repo.Samples, "LE1 Mod Collection (2026) - 201.biq2")).Mods[0];
        Assert.Equal("LE1 Community Patch", m.Name);
        Assert.Equal("masseffectlegendaryedition", m.GameDomain);
        Assert.Equal(23, m.ModId);
        Assert.Equal(12218, m.FileId);
        Assert.Equal("LE1/LE1 Community Patch/moddesc.ini", m.ModDescRelPath);
        Assert.Equal("7ab7bc2c343bf62d13c068ffe685565f", m.ModDescMd5);
    }

    [Fact]
    public void WithLinks_fills_missing_links_only()
    {
        var g = BiqGroup.Load(Path.Combine(Repo.Samples, "LE2 Mod Collection (2026) - 201.biq2"));
        var links = new Dictionary<string, string>
        {
            ["A Lot of Videos for LE2"] = "https://www.nexusmods.com/masseffectlegendaryedition/mods/3?tab=files&file_id=13305",
            ["Unofficial LE2 Patch"] = "https://www.nexusmods.com/masseffectlegendaryedition/mods/1?tab=files&file_id=999", // already has a link: must not change
        };
        var fixedGroup = g.WithLinks(links);
        Assert.Equal(82, fixedGroup.Mods.Count(m => m.Downloadable));
        Assert.Equal(13305, fixedGroup.Mods.Single(m => m.Name == "A Lot of Videos for LE2").FileId);
        Assert.Equal(12199, fixedGroup.Mods.Single(m => m.Name == "Unofficial LE2 Patch").FileId);
    }

    [Theory]
    [InlineData("..\\\\evil\\\\moddesc.ini")]
    [InlineData("C:\\\\evil\\\\moddesc.ini")]
    [InlineData("/etc/moddesc.ini")]
    public void Rejects_unsafe_paths(string path)
    {
        var json = $$"""{"game":"LE1","mods":[{"modname":"x","moddescpath":"{{path}}","moddeschash":"00","moddescsize":1,"downloadlink":null}]}""";
        Assert.Throws<InvalidDataException>(() => BiqGroup.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Tolerates_utf8_bom()
    {
        var json = """{"game":"LE1","mods":[]}"""u8.ToArray();
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(json).ToArray();
        Assert.Equal("LE1", BiqGroup.Parse(withBom).Game);
    }
}
