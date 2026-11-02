using System.Net;
using System.Text;
using Mele.Engine.Biq;
using Mele.Engine.Download;
using Mele.Engine.Nexus;

namespace Mele.Tests;

public class NexusAndDownloadTests
{
    private const string FileJson = """{"file_id":12218,"file_name":"LE1 Community Patch-23-2.7z","name":"LE1CP","size_in_bytes":5,"md5":"5d41402abc4b2a76b9719d911017c592","category_name":"MAIN"}""";

    private static NexusClient Client(FakeHandler h) => new(new HttpClient(h), "SECRET-KEY");

    [Fact]
    public async Task Sends_api_key_and_parses_user()
    {
        var h = new FakeHandler().On("users/validate.json", """{"name":"Tester","is_premium":true}""");
        var user = await Client(h).ValidateAsync();
        Assert.Equal(("Tester", true), (user.Name, user.IsPremium));
        Assert.Equal("SECRET-KEY", h.Requests[0].Headers.GetValues("apikey").Single());
        Assert.Equal("MELEAutoModInstaller", h.Requests[0].Headers.GetValues("Application-Name").Single());
    }

    [Fact]
    public async Task Http_errors_become_NexusException_without_leaking_the_key()
    {
        var h = new FakeHandler().On("files", """{"message":"nope"}""", HttpStatusCode.Forbidden);
        var ex = await Assert.ThrowsAsync<NexusException>(() => Client(h).GetFileInfoAsync("g", 1, 2));
        Assert.Contains("403", ex.Message);
        Assert.DoesNotContain("SECRET-KEY", ex.Message);
    }

    [Fact]
    public async Task Download_url_with_spaces_is_usable()
    {
        var h = new FakeHandler().On("download_link.json", """[{"URI":"https://cdn.example/cdn/1/LE1 Community Patch-23.7z?expires=1&md5=a"}]""");
        var uri = await Client(h).GetDownloadUrlAsync("g", 23, 12218);
        Assert.Contains("%20", uri.AbsoluteUri);
        Assert.DoesNotContain(" ", uri.AbsoluteUri);
    }

    [Fact]
    public async Task Fetch_downloads_verifies_then_serves_from_cache()
    {
        using var tmp = new TempDir();
        var nexus = new FakeHandler()
            .On("files/12218.json", FileJson)
            .On("download_link.json", """[{"URI":"https://cdn.example/a b.7z"}]""");
        var cdn = new FakeHandler().On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("hello")) });
        var dl = new Downloader(Client(nexus), new HttpClient(cdn), tmp.Path);

        var (path, result) = await dl.FetchAsync("masseffectlegendaryedition", 23, 12218);
        Assert.Equal(FetchResult.Downloaded, result);
        Assert.Equal("hello", File.ReadAllText(path));

        var (_, again) = await dl.FetchAsync("masseffectlegendaryedition", 23, 12218);
        Assert.Equal(FetchResult.Cached, again);
        Assert.Single(cdn.Requests); // second call never hit the CDN
    }

    [Fact]
    public async Task Fetch_rejects_wrong_size_and_leaves_no_cache_entry()
    {
        using var tmp = new TempDir();
        var nexus = new FakeHandler().On("files/12218.json", FileJson).On("download_link.json", """[{"URI":"https://cdn.example/x.7z"}]""");
        var cdn = new FakeHandler().On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("toolong")) });
        var dl = new Downloader(Client(nexus), new HttpClient(cdn), tmp.Path);

        await Assert.ThrowsAsync<NexusException>(() => dl.FetchAsync("g", 23, 12218));
        Assert.Null(dl.FindCached(12218));
        Assert.Empty(Directory.EnumerateFiles(tmp.Path, "*.part", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(1024, true)]  // advertised as 1 KB
    [InlineData(1900, true)]  // Nexus rounds down, so the real file can be up to 1023 bytes bigger
    [InlineData(2048, false)] // clearly the wrong file
    public async Task Size_given_only_in_kb_is_accepted_within_the_rounding_gap(int actualBytes, bool accepted)
    {
        using var tmp = new TempDir();
        const string kbOnly = """{"file_id":12218,"file_name":"a.7z","name":"A","size_kb":1,"category_name":"MAIN"}""";
        var nexus = new FakeHandler().On("files/12218.json", kbOnly).On("download_link.json", """[{"URI":"https://cdn.example/x.7z"}]""");
        var cdn = new FakeHandler().On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[actualBytes]) });
        var dl = new Downloader(Client(nexus), new HttpClient(cdn), tmp.Path);

        if (accepted) Assert.Equal(FetchResult.Downloaded, (await dl.FetchAsync("g", 23, 12218)).Result);
        else await Assert.ThrowsAsync<NexusException>(() => dl.FetchAsync("g", 23, 12218));
    }
}
