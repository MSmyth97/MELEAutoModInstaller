using System.Net;
using System.Text;
using Mele.Engine.Provision;

namespace Mele.Tests;

public class ProvisionTests
{
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("extractor-bytes");

    private static string ReleasesJson(long size = -1, bool newestIsPrerelease = true)
    {
        if (size < 0) size = Payload.Length;
        string Asset(string v) => $$"""{"name":"ME3TweaksModManagerExtractor_{{v}}.exe","size":{{size}},"browser_download_url":"https://dl.example/{{v}}.exe"}""";
        return $$"""
            [
              {"tag_name":"9.3.0.1","draft":false,"prerelease":{{newestIsPrerelease.ToString().ToLowerInvariant()}},"assets":[{{Asset("9.3.0.1")}}]},
              {"tag_name":"9.2.1.137","draft":false,"prerelease":false,"assets":[
                 {"name":"upd-abc.lzma","size":1,"browser_download_url":"https://dl.example/upd"},
                 {{Asset("9.2.1.137")}}]}
            ]
            """;
    }

    private static FakeHandler Feed(string releases) => new FakeHandler()
        .On(r => r.RequestUri!.Host == "api.github.com", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releases) })
        .On(r => r.RequestUri!.Host == "dl.example", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });

    private static readonly Dictionary<string, string> FakeM3 = new() { ["ME3TweaksModManager/ME3TweaksModManager.exe"] = "MZ-fake-m3" };

    [Fact]
    public async Task Installs_m3_from_the_newest_stable_release_and_creates_the_library_folder()
    {
        using var tmp = new TempDir();
        var home = Path.Combine(tmp.Path, "ME3TweaksModManager");
        var feed = Feed(ReleasesJson());
        var m3 = new M3Installer(new HttpClient(feed), new FakeExtractor(FakeM3), home);

        var exe = await m3.EnsureAsync();
        Assert.Equal(Path.Combine(home, "ME3TweaksModManager.exe"), exe);
        Assert.Equal("MZ-fake-m3", File.ReadAllText(exe));
        Assert.True(Directory.Exists(m3.LibraryPath));
        Assert.Contains(feed.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("9.2.1.137.exe")); // not the 9.3.0.1 prerelease
        Assert.DoesNotContain(feed.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("9.3.0.1.exe"));
        Assert.Empty(Directory.GetDirectories(home).Where(d => Path.GetFileName(d).StartsWith(".install-"))); // work folder cleaned
    }

    [Fact]
    public async Task Never_overwrites_an_existing_install_or_touches_the_network()
    {
        using var tmp = new TempDir();
        var home = Path.Combine(tmp.Path, "m3");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "ME3TweaksModManager.exe"), "USERS-OWN-COPY");
        var feed = Feed(ReleasesJson());
        await new M3Installer(new HttpClient(feed), new FakeExtractor(FakeM3), home).EnsureAsync();
        Assert.Equal("USERS-OWN-COPY", File.ReadAllText(Path.Combine(home, "ME3TweaksModManager.exe")));
        Assert.Empty(feed.Requests);
    }

    [Fact]
    public async Task Rejects_a_download_whose_size_differs_and_leaves_nothing_installed()
    {
        using var tmp = new TempDir();
        var home = Path.Combine(tmp.Path, "m3");
        var m3 = new M3Installer(new HttpClient(Feed(ReleasesJson(size: 999))), new FakeExtractor(FakeM3), home);
        await Assert.ThrowsAsync<ProvisionException>(() => m3.EnsureAsync());
        Assert.False(m3.IsInstalled);
    }

    [Fact]
    public async Task Fails_clearly_when_the_archive_does_not_contain_m3()
    {
        using var tmp = new TempDir();
        var m3 = new M3Installer(new HttpClient(Feed(ReleasesJson())), new FakeExtractor(new() { ["readme.txt"] = "x" }), Path.Combine(tmp.Path, "m3"));
        var ex = await Assert.ThrowsAsync<ProvisionException>(() => m3.EnsureAsync());
        Assert.Contains("ME3TweaksModManager.exe", ex.Message);
        Assert.False(m3.IsInstalled);
    }

    // ---- prefix ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Prefix_is_created_with_umu_wineboot_and_the_expected_environment()
    {
        using var tmp = new TempDir();
        var root = Path.Combine(tmp.Path, "prefix");
        IReadOnlyDictionary<string, string>? seenEnv = null;
        var runner = new EnvCapturingRunner((exe, args, env) =>
        {
            seenEnv = env;
            Assert.Equal("/usr/bin/umu-run", exe);
            Assert.Equal(["wineboot", "-u"], args);
            File.WriteAllText(Path.Combine(root, "user.reg"), "WINE REGISTRY Version 2\n"); // what wineboot would create
            return 0;
        });
        var prefix = new ProtonPrefix(root, runner, () => "/usr/bin/umu-run");
        await prefix.EnsureAsync();

        Assert.Equal(root, seenEnv!["WINEPREFIX"]);
        Assert.Equal("GE-Proton", seenEnv["PROTONPATH"]);
        Assert.True(prefix.IsInitialized);
        Assert.True(Directory.Exists(prefix.M3Home));
        Assert.EndsWith(Path.Combine("Documents", "ME3TweaksModManager"), prefix.M3Home);

        runner.Calls = 0;
        await prefix.EnsureAsync();            // already initialised: must not run umu again
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Missing_umu_or_a_failed_wineboot_give_actionable_errors()
    {
        using var tmp = new TempDir();
        var noUmu = new ProtonPrefix(Path.Combine(tmp.Path, "a"), new EnvCapturingRunner((_, _, _) => 0), () => null);
        Assert.Contains("umu-launcher", (await Assert.ThrowsAsync<ProvisionException>(() => noUmu.EnsureAsync())).Message);

        var failing = new ProtonPrefix(Path.Combine(tmp.Path, "b"), new EnvCapturingRunner((_, _, _) => 1), () => "/usr/bin/umu-run");
        Assert.Contains("exit 1", (await Assert.ThrowsAsync<ProvisionException>(() => failing.EnsureAsync())).Message);
    }

    [Fact]
    public void User_dir_prefers_steamuser_and_ignores_public()
    {
        using var tmp = new TempDir();
        var p = new ProtonPrefix(tmp.Path, new EnvCapturingRunner((_, _, _) => 0));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "drive_c", "users", "Public"));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "drive_c", "users", "matt"));
        Assert.EndsWith("matt", p.UserDir());                 // only real profile present
        Directory.CreateDirectory(Path.Combine(tmp.Path, "drive_c", "users", "steamuser"));
        Assert.EndsWith("steamuser", p.UserDir());
    }
}

internal sealed class EnvCapturingRunner(Func<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>?, int> behave) : Mele.Engine.Mem.IProcessRunner
{
    public int Calls { get; set; }
    public Task<int> RunAsync(string exe, IReadOnlyList<string> args, Action<string>? onLine = null, IReadOnlyDictionary<string, string>? env = null, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(behave(exe, args, env));
    }
}
