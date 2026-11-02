using System.Diagnostics;
using System.Text;

namespace Mele.Engine.Mem;

public interface IProcessRunner
{
    Task<int> RunAsync(string exe, IReadOnlyList<string> args, Action<string>? onLine = null,
        IReadOnlyDictionary<string, string>? env = null, CancellationToken ct = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(string exe, IReadOnlyList<string> args, Action<string>? onLine = null,
        IReadOnlyDictionary<string, string>? env = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // The Windows build of MEM writes UTF-16 (it switches stdout to _O_U16TEXT); the Unix builds write UTF-8.
            StandardOutputEncoding = OperatingSystem.IsWindows() ? Encoding.Unicode : Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;

        using var p = new Process { StartInfo = psi };
        void Handler(object _, DataReceivedEventArgs e) { if (e.Data != null) onLine?.Invoke(e.Data.TrimEnd('\r')); }
        p.OutputDataReceived += Handler;
        p.ErrorDataReceived += Handler;
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }))
            await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return p.ExitCode;
    }
}
