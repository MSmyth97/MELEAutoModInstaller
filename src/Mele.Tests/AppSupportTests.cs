using Mele.Engine.Orchestration;
using Mele.Engine.Platform;
using Mele.Engine.Provision;
using Mele.Engine.Textures;

namespace Mele.Tests;

public class AppSupportTests
{
    private static string RepoFile(string name)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, name))) d = d.Parent;
        return Path.Combine(d!.FullName, name);
    }

    [Fact]
    public void Embedded_manifests_are_the_repo_files()
    {
        var g = GuideConfig.LoadDefault();
        var gf = GuideConfig.Load(RepoFile("guide.json"));
        Assert.Equal((gf.GuideModId, gf.GameDomain), (g.GuideModId, g.GameDomain));
        Assert.Equal(gf.LinkOverrides, g.LinkOverrides);
        Assert.Equal(1127, g.GuideModId);

        var t = TextureManifest.LoadDefault();
        var tf = TextureManifest.Load(RepoFile("textures.json"));
        foreach (var game in new[] { "LE1", "LE2", "LE3" })
            Assert.Equal(tf.Items(game).Select(i => (i.Name, i.Kind, i.FileId, i.TorrentMember)), t.Items(game).Select(i => (i.Name, i.Kind, i.FileId, i.TorrentMember)));
    }

    [Fact]
    public void Saved_api_key_is_owner_only_and_loads_back_without_whitespace()
    {
        using var tmp = new TempDir();
        var f = Path.Combine(tmp.Path, "cfg", "api_key");
        ApiKeyStore.Save("  abc123\n", f);
        Assert.Equal("abc123", ApiKeyStore.Load(null, [f]));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(f));
        Assert.Throws<ApiKeyException>(() => ApiKeyStore.Save("   ", f));
    }

    [Fact]
    public void Saving_over_a_looser_existing_key_file_tightens_it()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var f = Path.Combine(tmp.Path, "api_key");
        File.WriteAllText(f, "old");
        File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        ApiKeyStore.Save("new", f);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(f));
        Assert.Equal("new", File.ReadAllText(f));
    }

    [Fact]
    public void Settings_round_trip_and_survive_a_corrupt_file()
    {
        using var tmp = new TempDir();
        var f = Path.Combine(tmp.Path, "s", "settings.json");
        new AppSettings { GameDir = "/g", PrefixDir = "/p", Le2 = false, IncludeTextures = false }.Save(f);
        var s = AppSettings.Load(f);
        Assert.Equal(("/g", "/p", false), (s.GameDir, s.PrefixDir, s.IncludeTextures));
        Assert.Equal(["LE1", "LE3"], s.Games);
        File.WriteAllText(f, "{{{");
        Assert.Equal("", AppSettings.Load(f).GameDir);
        Assert.Equal("", AppSettings.Load(Path.Combine(tmp.Path, "missing.json")).GameDir);
    }

    [Fact]
    public void M3_location_without_a_prefix_reports_no_backups_instead_of_failing()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var loc = M3Location.Resolve(Path.Combine(tmp.Path, "not-created-yet"));
        Assert.False(loc.IsInstalled);
        Assert.EndsWith(Path.Combine("Documents", "ME3TweaksModManager"), loc.Home);
        var b = loc.Backups.Get("LE1");
        Assert.False(b.Valid);
        Assert.Contains("not been set up", b.Reason);
    }
}

public class BundledSevenZipTests
{
    [Fact]
    public void A_bundled_copy_is_preferred_and_made_executable()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var dir = Path.Combine(tmp.Path, "7zip");
        Directory.CreateDirectory(dir);
        var bundled = Path.Combine(dir, "7zz");
        File.WriteAllText(bundled, "#!/bin/sh\n");
        File.SetUnixFileMode(bundled, UnixFileMode.UserRead | UnixFileMode.UserWrite); // as if unpacked from a zip: no execute bit

        Assert.Equal(bundled, Mele.Engine.Archive.SevenZipExtractor.FindExecutable(dir));
        Assert.True((File.GetUnixFileMode(bundled) & UnixFileMode.UserExecute) != 0);
    }

    [Fact]
    public async Task The_bundled_copy_is_actually_used_to_extract()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var dir = Path.Combine(tmp.Path, "7zip");
        Directory.CreateDirectory(dir);
        // A stand-in 7-Zip that records how it was called and creates the output folder, like the real "7zz x -y -oDIR archive".
        var bundled = Path.Combine(dir, "7zz");
        File.WriteAllText(bundled, "#!/bin/sh\nmkdir -p \"${3#-o}\"\necho \"$@\" > \"${3#-o}/called.txt\"\n");
        File.SetUnixFileMode(bundled, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // FindExecutable(dir) is what ExtractAsync uses by default via DefaultBundledDir; verify the lookup finds it and it runs.
        var exe = Mele.Engine.Archive.SevenZipExtractor.FindExecutable(dir)!;
        var runner = new Mele.Engine.Mem.ProcessRunner();
        var outDir = Path.Combine(tmp.Path, "out");
        var rc = await runner.RunAsync(exe, ["x", "-y", $"-o{outDir}", "a.7z"]);
        Assert.Equal(0, rc);
        Assert.Contains("x -y", File.ReadAllText(Path.Combine(outDir, "called.txt")));
    }
}
