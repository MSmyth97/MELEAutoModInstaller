using Mele.Engine.Biq;
using Mele.Engine.Stage;

namespace Mele.Tests;

public class StagerTests
{
    private const string Ini = "[ModInfo]\nmodname = Test\n";
    private static string Md5Of(string s) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static ModEntry Entry(string name, string md5, string folder = "Test Mod") =>
        new(name, "g", 1, 2, $"LE1/{folder}/moddesc.ini", md5, 1, null);

    private static (TempDir root, string library) Library()
    {
        var root = new TempDir();
        var lib = Path.Combine(root.Path, "ME3TweaksModManager", "mods");
        Directory.CreateDirectory(lib);
        return (root, lib);
    }

    [Fact]
    public async Task Stages_the_folder_whose_moddesc_hash_matches()
    {
        var (root, lib) = Library();
        using var _ = root;
        var files = new Dictionary<string, string>
        {
            ["Wrapper/Test Mod/moddesc.ini"] = Ini,
            ["Wrapper/Test Mod/DLC_MOD_X/file.bin"] = "data",
            ["Wrapper/Other/moddesc.ini"] = "[ModInfo]\nmodname = Other\n",
        };
        var status = await new Stager(new FakeExtractor(files)).StageAsync("a.7z", lib, Entry("Test", Md5Of(Ini)));
        Assert.Equal(StageStatus.Ok, status);
        Assert.True(File.Exists(Path.Combine(lib, "LE1", "Test Mod", "DLC_MOD_X", "file.bin")));
        Assert.False(Directory.Exists(Path.Combine(lib, "LE1", "Other")));
        Assert.True(Stager.IsStaged(lib, Entry("Test", Md5Of(Ini))));
    }

    [Fact]
    public async Task Flags_version_drift_but_still_installs_by_folder_name()
    {
        var (root, lib) = Library();
        using var _ = root;
        var files = new Dictionary<string, string> { ["Test Mod/moddesc.ini"] = "[ModInfo]\nmodname = Test\nmodver = 2\n", ["Test Mod/x.txt"] = "x" };
        var status = await new Stager(new FakeExtractor(files)).StageAsync("a.7z", lib, Entry("Test", Md5Of(Ini)));
        Assert.Equal(StageStatus.OkVersionDiffers, status);
        Assert.True(File.Exists(Path.Combine(lib, "LE1", "Test Mod", "x.txt")));
    }

    [Fact]
    public async Task Without_a_recorded_hash_accepts_a_single_moddesc_only()
    {
        var (root, lib) = Library();
        using var _ = root;
        var one = new Dictionary<string, string> { ["M3TO/moddesc.ini"] = Ini, ["M3TO/DLC_MOD_ALOT/a"] = "a" };
        Assert.Equal(StageStatus.Ok, await new Stager(new FakeExtractor(one)).StageAsync("a.7z", lib, Entry("ALOT", "")));

        var two = new Dictionary<string, string> { ["A/moddesc.ini"] = Ini, ["B/moddesc.ini"] = Ini };
        await Assert.ThrowsAsync<StageException>(() => new Stager(new FakeExtractor(two)).StageAsync("a.7z", lib, Entry("ALOT", "")));
    }

    [Fact]
    public async Task Archive_without_moddesc_fails_clearly()
    {
        var (root, lib) = Library();
        using var _ = root;
        var ex = await Assert.ThrowsAsync<StageException>(() =>
            new Stager(new FakeExtractor(new() { ["readme.txt"] = "hi" })).StageAsync("a.7z", lib, Entry("Test", Md5Of(Ini))));
        Assert.Contains("no moddesc.ini", ex.Message);
    }

    [Fact]
    public async Task Leaves_no_scratch_folders_behind_and_replaces_an_existing_install()
    {
        var (root, lib) = Library();
        using var _ = root;
        Directory.CreateDirectory(Path.Combine(lib, "LE1", "Test Mod"));
        File.WriteAllText(Path.Combine(lib, "LE1", "Test Mod", "stale.txt"), "old");
        await new Stager(new FakeExtractor(new() { ["Test Mod/moddesc.ini"] = Ini })).StageAsync("a.7z", lib, Entry("Test", Md5Of(Ini)));
        Assert.False(File.Exists(Path.Combine(lib, "LE1", "Test Mod", "stale.txt")));
        var scratch = Path.Combine(Path.GetDirectoryName(lib)!, ".mele-installer-tmp");
        Assert.Empty(Directory.Exists(scratch) ? Directory.GetDirectories(scratch) : []);
    }
}
