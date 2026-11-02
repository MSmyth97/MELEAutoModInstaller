using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mele.Engine.Archive;

public interface IArchiveExtractor
{
    Task ExtractAsync(string archive, string destDir, CancellationToken ct = default);
}

public sealed class ArchiveException(string message) : Exception(message);

/// <summary>Extracts zip/7z/rar by shelling out to 7-Zip (7zz / 7z / 7za), falling back to bsdtar on Unix.</summary>
public sealed class SevenZipExtractor : IArchiveExtractor
{
    private static readonly string[] Candidates = ["7zz", "7z", "7za", "7zr"];

    public static string? FindExecutable()
    {
        var exts = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new[] { ".exe", "" } : new[] { "" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip"));
        foreach (var dir in dirs)
            foreach (var name in Candidates)
                foreach (var ext in exts)
                {
                    var p = Path.Combine(dir, name + ext);
                    if (File.Exists(p)) return p;
                }
        return null;
    }

    public async Task ExtractAsync(string archive, string destDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        var sevenZip = FindExecutable();
        if (sevenZip != null && await RunAsync(sevenZip, ["x", "-y", $"-o{destDir}", archive], ct) == 0) return;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && await RunAsync("bsdtar", ["-xf", archive, "-C", destDir], ct) == 0) return;
        throw new ArchiveException($"Could not extract {Path.GetFileName(archive)} (need 7-Zip, or bsdtar on Linux/macOS)");
    }

    private static async Task<int> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            // Drain both streams so a chatty extractor can't block on a full pipe.
            var drain = Task.WhenAll(p.StandardOutput.ReadToEndAsync(ct), p.StandardError.ReadToEndAsync(ct));
            await p.WaitForExitAsync(ct);
            await drain;
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception) { return -1; } // executable not found
    }
}
