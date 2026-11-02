using Mele.Engine.Stage;
using Mele.Engine.Textures;

namespace Mele.Tests;

public class TextureTests
{
    private static string ManifestPath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "textures.json"))) dir = dir.Parent;
            return Path.Combine(dir!.FullName, "textures.json");
        }
    }

    [Fact]
    public void Manifest_matches_the_guide_counts_and_order()
    {
        var m = TextureManifest.Load(ManifestPath);
        Assert.Equal((6, 5, 6), (m.Items("LE1").Count, m.Items("LE2").Count, m.Items("LE3").Count));
        // The guide installs ALOT, then ISL, then the .mem mods.
        Assert.Equal(["ALOT for LE1", "Improved Static Lighting (ISL) for LE1"], m.Items("LE1").Take(2).Select(i => i.Name));
        Assert.All(m.Items("LE1").Take(2), i => Assert.Equal(TextureKind.M3to, i.Kind));
        Assert.Equal(12, m.Items("LE1").Single(i => i.Name == "LEUITM").Select!.Count);
        Assert.Equal(22, m.Items("LE2").Single(i => i.Name == "LEUITM2").Select!.Count);
        // Install file only: the HUD archive also ships an uninstaller that must never be installed.
        Assert.Equal(["ReducedInteractionHUD.mem"], m.Items("LE3").Single(i => i.Name.StartsWith("Reduced")).Select);
        // Arm patch: user chose X05.
        Assert.Contains("X05", m.Items("LE1").Single(i => i.Name.StartsWith("Liara")).Select!.Single());
        Assert.Equal(12950, m.Torrent.FileId);
    }

    [Fact]
    public async Task MemFiles_extracts_only_the_selected_files()
    {
        using var tmp = new TempDir();
        var files = new Dictionary<string, string> { ["a/Keep.mem"] = "1", ["a/UninstallHUD.mem"] = "2", ["a/readme.txt"] = "3" };
        var dest = Path.Combine(tmp.Path, "LE3", "06 HUD");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var got = await MemFiles.ExtractAsync(new FakeExtractor(files), "x.zip", dest, ["keep.mem"]); // case-insensitive
        Assert.Equal(["Keep.mem"], got.Select(Path.GetFileName));
        Assert.False(File.Exists(Path.Combine(dest, "UninstallHUD.mem")));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(dest)!, ".extract-*")); // scratch cleaned
    }

    [Fact]
    public async Task MemFiles_fails_loudly_when_a_selected_file_is_missing()
    {
        using var tmp = new TempDir();
        var dest = Path.Combine(tmp.Path, "d");
        var ex = await Assert.ThrowsAsync<StageException>(() =>
            MemFiles.ExtractAsync(new FakeExtractor(new() { ["a.mem"] = "1" }), "x.zip", dest, ["a.mem", "b.mem"]));
        Assert.Contains("b.mem", ex.Message);
    }

    [Fact]
    public void OrderedFiles_follow_the_guide_not_the_alphabet_and_skip_m3to()
    {
        using var tmp = new TempDir();
        var m = TextureManifest.Load(ManifestPath);
        // Lay out LE3's .mem folders as the pipeline would (indices count the two... LE3 has 1 m3to item, so mem items start at 2).
        var items = m.Items("LE3");
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Kind != TextureKind.Mem) continue;
            var dir = MemFiles.ItemDir(tmp.Path, "LE3", i + 1, items[i]);
            Directory.CreateDirectory(dir);
            foreach (var n in items[i].Select ?? ["x.mem"]) File.WriteAllText(Path.Combine(dir, n), "");
        }
        var ordered = MemFiles.OrderedFiles(m, "LE3", tmp.Path).Select(Path.GetFileName).ToList();
        Assert.Equal(["x.mem", "x.mem", "x.mem", "ReducedInteractionHUD.mem"], ordered);
    }

    [Fact]
    public void Mfl_is_crlf_utf8_without_bom_and_maps_paths()
    {
        using var tmp = new TempDir();
        var mfl = Path.Combine(tmp.Path, "LE1.mfl");
        MemFiles.WriteMfl(mfl, ["/a/b.mem", "/c/d.mem"], p => "Z:" + p.Replace('/', '\\'));
        var bytes = File.ReadAllBytes(mfl);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("Z:\\a\\b.mem\r\nZ:\\c\\d.mem\r\n", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void BatchWriter_uses_the_staged_inis_hash_and_modname()
    {
        using var tmp = new TempDir();
        var lib = Path.Combine(tmp.Path, "mods");
        var m = TextureManifest.Load(ManifestPath);
        foreach (var name in new[] { "ALOT for LE2", "Improved Static Lighting (ISL) for LE2" })
        {
            var d = Path.Combine(lib, "LE2", name);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "moddesc.ini"), $"[ModInfo]\nmodname = Real Name of {name}\n");
        }
        var path = M3toBatchWriter.Write(m, "LE2", lib, Path.Combine(lib, "BatchModQueues"));

        var group = Mele.Engine.Biq.BiqGroup.Load(path); // the writer's output must be readable by our own parser (and M3's format)
        Assert.Equal(2, group.Mods.Count);
        Assert.Equal("LE2/ALOT for LE2/moddesc.ini", group.Mods[0].ModDescRelPath);
        Assert.Equal("Real Name of ALOT for LE2", group.Mods[0].Name);
        Assert.Equal(13764, group.Mods[0].FileId);
        Assert.Equal(32, group.Mods[1].ModDescMd5.Length);
        Assert.True(Stager.IsStaged(lib, group.Mods[0])); // hash we wrote matches the file on disk
        Assert.Contains("\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void BatchWriter_refuses_when_a_mod_is_not_staged()
    {
        using var tmp = new TempDir();
        Assert.Throws<StageException>(() => M3toBatchWriter.Write(TextureManifest.Load(ManifestPath), "LE1", tmp.Path, tmp.Path));
    }
}
