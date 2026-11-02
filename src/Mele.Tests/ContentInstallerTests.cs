using Mele.Engine.Biq;
using Mele.Engine.Download;
using Mele.Engine.Nexus;
using Mele.Engine.Pipeline;
using Mele.Engine.Platform;
using Mele.Engine.Stage;

namespace Mele.Tests;

internal sealed class FakeSource(Func<ModEntry, string> pathFor) : IModFileSource
{
    public List<int> Fetched { get; } = [];
    public List<int> Evicted { get; } = [];
    public Task<(string Path, FetchResult Result)> FetchAsync(ModEntry entry, CancellationToken ct = default)
    {
        lock (Fetched) Fetched.Add(entry.FileId!.Value);
        return Task.FromResult((pathFor(entry), FetchResult.Downloaded));
    }
    public void Evict(int fileId) { lock (Evicted) Evicted.Add(fileId); }
}

public class ContentInstallerTests
{
    private const string Ini = "[ModInfo]\nmodname = M\n";
    private static string Md5Of(string s) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static ModEntry Mod(string name, int? file, string md5 = "") =>
        new(name, file == null ? null : "g", file == null ? null : 1, file, $"LE1/{name}/moddesc.ini", md5, 1, null);

    private static (TempDir root, string lib) Lib()
    {
        var r = new TempDir();
        var lib = Path.Combine(r.Path, "ME3TweaksModManager", "mods");
        Directory.CreateDirectory(lib);
        return (r, lib);
    }

    [Fact]
    public async Task Installs_missing_skips_staged_and_flags_manual()
    {
        var (root, lib) = Lib();
        using var _r = root;
        Directory.CreateDirectory(Path.Combine(lib, "LE1", "Have"));
        File.WriteAllText(Path.Combine(lib, "LE1", "Have", "moddesc.ini"), Ini);
        var group = new BiqGroup("LE1", "", [Mod("Have", 1, Md5Of(Ini)), Mod("Need", 2, Md5Of(Ini)), Mod("Manual", null, "ff")]);
        var source = new FakeSource(_ => "a.7z");
        var inst = new ContentInstaller(source, new Stager(new FakeExtractor(new() { ["Need/moddesc.ini"] = Ini })));

        var results = (await inst.InstallAsync(group, lib)).ToDictionary(r => r.Mod.Name, r => r.Status);
        Assert.Equal(ContentStatus.AlreadyStaged, results["Have"]);
        Assert.Equal(ContentStatus.Installed, results["Need"]);
        Assert.Equal(ContentStatus.NeedsManualDownload, results["Manual"]);
        Assert.Equal([2], source.Fetched);          // the staged mod was never fetched
        Assert.Equal([2], source.Evicted);          // big archives are removed after staging
        Assert.True(File.Exists(Path.Combine(lib, "LE1", "Need", "moddesc.ini")));
    }

    [Fact]
    public async Task A_failure_is_reported_for_that_mod_and_does_not_stop_the_others()
    {
        var (root, lib) = Lib();
        using var _r = root;
        var group = new BiqGroup("LE1", "", [Mod("Bad", 1, "aa"), Mod("Good", 2, Md5Of(Ini))]);
        var source = new FakeSource(e => e.FileId == 1 ? throw new NexusException("HTTP 403") : "a.7z");
        var inst = new ContentInstaller(source, new Stager(new FakeExtractor(new() { ["Good/moddesc.ini"] = Ini })));

        var results = (await inst.InstallAsync(group, lib, jobs: 1)).ToDictionary(r => r.Mod.Name);
        Assert.Equal(ContentStatus.Failed, results["Bad"].Status);
        Assert.Contains("403", results["Bad"].Note);
        Assert.Equal(ContentStatus.Installed, results["Good"].Status);
        Assert.DoesNotContain(1, source.Evicted);   // a failed mod's archive is kept for the retry
    }

    [Fact]
    public async Task Keep_archives_and_filter_are_honoured_and_version_drift_is_visible()
    {
        var (root, lib) = Lib();
        using var _r = root;
        var group = new BiqGroup("LE1", "", [Mod("Alpha", 1, "00"), Mod("Beta", 2, "00")]); // recorded hash won't match
        var source = new FakeSource(_ => "a.7z");
        var inst = new ContentInstaller(source, new Stager(new FakeExtractor(new() { ["Alpha/moddesc.ini"] = Ini })));

        var results = await inst.InstallAsync(group, lib, keepArchives: true, filter: m => m.Name == "Alpha");
        Assert.Single(results);
        Assert.Equal(ContentStatus.InstalledVersionDiffers, results[0].Status);
        Assert.Empty(source.Evicted);
    }

    [Fact]
    public void Survey_reports_staged_before_manual_so_unlinked_but_present_mods_count_as_staged()
    {
        var (root, lib) = Lib();
        using var _r = root;
        Directory.CreateDirectory(Path.Combine(lib, "LE1", "Videos"));
        File.WriteAllText(Path.Combine(lib, "LE1", "Videos", "moddesc.ini"), Ini);
        var s = ContentInstaller.Survey(new BiqGroup("LE1", "", [Mod("Videos", null, Md5Of(Ini)), Mod("Absent", null, "ff"), Mod("Todo", 5, "ff")]), lib);
        Assert.Equal([ContentStatus.AlreadyStaged, ContentStatus.NeedsManualDownload, ContentStatus.WouldInstall], s.Select(x => x.Status));
    }
}

public class ApiKeyStoreTests
{
    [Fact]
    public void Environment_wins_then_files_in_order_and_a_missing_key_explains_itself()
    {
        using var tmp = new TempDir();
        var f1 = Path.Combine(tmp.Path, "one"); var f2 = Path.Combine(tmp.Path, "two");
        File.WriteAllText(f1, "  from-file-1\n"); File.WriteAllText(f2, "from-file-2");
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(f1, UnixFileMode.UserRead | UnixFileMode.UserWrite); File.SetUnixFileMode(f2, UnixFileMode.UserRead | UnixFileMode.UserWrite); }

        Assert.Equal("from-env", ApiKeyStore.Load(" from-env ", [f1, f2]));
        Assert.Equal("from-file-1", ApiKeyStore.Load(null, [f1, f2]));
        Assert.Equal("from-file-2", ApiKeyStore.Load("", [Path.Combine(tmp.Path, "missing"), f2]));
        Assert.Throws<ApiKeyException>(() => ApiKeyStore.Load(null, [Path.Combine(tmp.Path, "missing")]));
    }

    [Fact]
    public void A_key_file_readable_by_other_users_is_refused_without_leaking_the_key()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var f = Path.Combine(tmp.Path, "key");
        File.WriteAllText(f, "TOP-SECRET");
        File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        var ex = Assert.Throws<ApiKeyException>(() => ApiKeyStore.Load(null, [f]));
        Assert.DoesNotContain("TOP-SECRET", ex.Message);
        Assert.Contains("chmod 600", ex.Message);
    }
}
