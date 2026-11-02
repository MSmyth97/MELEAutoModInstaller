using System.Net;
using System.Text;
using Mele.Engine.Archive;

namespace Mele.Tests;

internal static class Repo
{
    public static string Samples
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "samples"))) dir = dir.Parent;
            return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("samples folder not found"), "samples");
        }
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mele-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

/// <summary>Routes requests by path suffix and records them.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];
    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeHandler On(string pathContains, string json, HttpStatusCode code = HttpStatusCode.OK) =>
        On(r => r.RequestUri!.AbsolutePath.Contains(pathContains), _ => new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    public FakeHandler On(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes.Add((match, respond));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        foreach (var (match, respond) in _routes)
            if (match(request)) return Task.FromResult(respond(request));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>"Extracts" by writing a prepared set of files, so tests need no real archives or 7-Zip.</summary>
internal sealed class FakeExtractor(Dictionary<string, string> files) : IArchiveExtractor
{
    public Task ExtractAsync(string archive, string destDir, CancellationToken ct = default)
    {
        foreach (var (rel, content) in files)
        {
            var p = System.IO.Path.Combine(destDir, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
            File.WriteAllText(p, content);
        }
        return Task.CompletedTask;
    }
}
