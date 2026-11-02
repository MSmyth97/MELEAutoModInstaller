using System.IO.Compression;
using System.Net;
using System.Text;
using Mele.Engine.Archive;
using Mele.Engine.Mem;

namespace Mele.Tests;

internal sealed class FakeRunner(Func<string, IReadOnlyList<string>, (int Code, string[] Lines)> behave) : IProcessRunner
{
    public List<(string Exe, string[] Args)> Calls { get; } = [];
    public Task<int> RunAsync(string exe, IReadOnlyList<string> args, Action<string>? onLine = null, IReadOnlyDictionary<string, string>? env = null, CancellationToken ct = default)
    {
        Calls.Add((exe, args.ToArray()));
        var (code, lines) = behave(exe, args);
        foreach (var l in lines) onLine?.Invoke(l);
        return Task.FromResult(code);
    }
}

public class MemTests
{
    [Fact]
    public async Task Version_is_parsed_and_a_missing_executable_is_null()
    {
        var ok = new MemRunner(new FakeRunner((_, _) => (0, ["banner", "", "Version: 534"])), "mem");
        Assert.Equal(534, await ok.VersionAsync());
        Assert.Null(await new MemRunner(new FakeRunner((_, _) => (1, ["Version: 9"])), "mem").VersionAsync()); // failure exit wins
        Assert.Null(await new MemRunner(new ProcessRunner(), "/definitely/not/here").VersionAsync());
    }

    [Fact]
    public async Task Install_uses_the_same_command_line_as_m3()
    {
        var fake = new FakeRunner((_, _) => (0, ["Texture installation started...", "Installation finished."]));
        var r = await new MemRunner(fake, "mem").InstallAsync("LE2", "/x/LE2.mfl");
        Assert.Equal(["--install-mods", "--gameid", "2", "--input", "/x/LE2.mfl", "--verify"], fake.Calls.Single().Args);
        Assert.True(r.Success);

        var fake2 = new FakeRunner((_, _) => (0, ["ok"]));
        await new MemRunner(fake2, "mem").InstallAsync("LE3", "f.mfl", skipMarkers: true);
        Assert.Equal("--skip-markers", fake2.Calls.Single().Args.Last());
    }

    [Fact]
    public async Task Install_is_not_successful_without_the_completion_line_even_on_exit_zero()
    {
        var r = await new MemRunner(new FakeRunner((_, _) => (0, ["Texture skipped."])), "mem").InstallAsync("LE1", "f.mfl");
        Assert.False(r.Success);
        Assert.False((await new MemRunner(new FakeRunner((_, _) => (1, ["Installation finished."])), "mem").InstallAsync("LE1", "f.mfl")).Success);
    }

    [Fact]
    public async Task SetGamePath_args_and_game_ids()
    {
        var fake = new FakeRunner((_, _) => (0, []));
        Assert.True(await new MemRunner(fake, "mem").SetGamePathAsync("LE1", "/g"));
        Assert.Equal(["--set-game-data-path", "--gameid", "1", "--path", "/g"], fake.Calls.Single().Args);
        Assert.Throws<ArgumentException>(() => MemRunner.GameId("ME1"));
    }

    [Fact]
    public async Task ProcessRunner_streams_lines_and_returns_the_exit_code()
    {
        if (OperatingSystem.IsWindows()) return;
        var lines = new List<string>();
        var rc = await new ProcessRunner().RunAsync("/bin/sh", ["-c", "echo one; echo two >&2; exit 3"], l => { lock (lines) lines.Add(l); });
        Assert.Equal(3, rc);
        Assert.Equal(["one", "two"], lines.Order());
    }

    // ---- updater -------------------------------------------------------------------------------------------

    private static (FakeHandler handler, string url) ReleaseFeed(int version, bool wrongSize = false)
    {
        // A stand-in "MEM": a shell script that answers --version, packaged like the real Linux release zip.
        var zip = new MemoryStream();
        using (var z = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = z.CreateEntry("MassEffectModderNoGui");
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write($"#!/bin/sh\necho 'MEM stand-in'\necho 'Version: {version}'\n");
        }
        var bytes = zip.ToArray();
        string Asset(string name, long size) => $$"""{"name":"{{name}}","size":{{size}},"browser_download_url":"https://dl.example/{{name}}"}""";
        var releases = $$"""
            [
              {"tag_name":"{{version + 1}}","draft":false,"prerelease":true,"assets":[{{Asset($"MassEffectModderNoGui-Linux-v{version + 1}.zip", 1)}}]},
              {"tag_name":"{{version}}","draft":false,"prerelease":false,"assets":[
                 {{Asset($"MassEffectModderNoGui-v{version}.7z", 5)}},
                 {{Asset($"MassEffectModderNoGui-macOS-v{version}.zip", 5)}},
                 {{Asset($"MassEffectModderNoGui-Linux-v{version}.zip", wrongSize ? bytes.Length + 1 : bytes.Length)}}]},
              {"tag_name":"{{version - 1}}","draft":false,"prerelease":false,"assets":[{{Asset($"MassEffectModderNoGui-Linux-v{version - 1}.zip", 1)}}]}
            ]
            """;
        var h = new FakeHandler()
            .On(r => r.RequestUri!.Host == "api.github.com", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releases) })
            .On(r => r.RequestUri!.AbsolutePath.EndsWith($"Linux-v{version}.zip"), _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        return (h, "");
    }

    [Fact]
    public async Task Updater_installs_the_newest_stable_linux_build_then_is_a_no_op()
    {
        if (OperatingSystem.IsWindows()) return; // the stand-in is a shell script
        using var tmp = new TempDir();
        var (handler, _) = ReleaseFeed(534);
        var up = new MemUpdater(new HttpClient(handler), new SevenZipExtractor(), new ProcessRunner(), Path.Combine(tmp.Path, "mem"));

        var exe = await up.EnsureAsync();
        Assert.Equal(534, await new MemRunner(new ProcessRunner(), exe).VersionAsync()); // not the prerelease 535, not 533
        Assert.True((File.GetUnixFileMode(exe) & UnixFileMode.UserExecute) != 0);
        Assert.Empty(Directory.GetDirectories(Path.Combine(tmp.Path, "mem"))); // work folder cleaned

        var downloads = handler.Requests.Count(r => r.RequestUri!.Host == "dl.example");
        await up.EnsureAsync();
        Assert.Equal(downloads, handler.Requests.Count(r => r.RequestUri!.Host == "dl.example")); // up to date: nothing re-downloaded
    }

    [Fact]
    public async Task Updater_rejects_a_download_whose_size_differs_from_the_release_listing()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var (handler, _) = ReleaseFeed(534, wrongSize: true);
        var up = new MemUpdater(new HttpClient(handler), new SevenZipExtractor(), new ProcessRunner(), Path.Combine(tmp.Path, "mem"));
        await Assert.ThrowsAsync<MemUpdateException>(() => up.EnsureAsync());
        Assert.False(File.Exists(up.ExePath));
    }

    [Theory]
    [InlineData("MassEffectModderNoGui-v534.7z", true, false, false)]
    [InlineData("MassEffectModderNoGui-Linux-v534.zip", false, true, false)]
    [InlineData("MassEffectModderNoGui-macOS-v534.zip", false, false, true)]
    [InlineData("MassEffectModder-v534.7z", false, false, false)] // the GUI build is never selected
    public void Asset_names_map_to_exactly_one_platform(string name, bool win, bool linux, bool mac)
    {
        Assert.Equal(win, MemUpdater.AssetMatcher(System.Runtime.InteropServices.OSPlatform.Windows)(name));
        Assert.Equal(linux, MemUpdater.AssetMatcher(System.Runtime.InteropServices.OSPlatform.Linux)(name));
        Assert.Equal(mac, MemUpdater.AssetMatcher(System.Runtime.InteropServices.OSPlatform.OSX)(name));
    }
}
