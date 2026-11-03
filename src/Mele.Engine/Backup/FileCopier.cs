namespace Mele.Engine.Backup;

internal static class FileCopier
{
    /// <summary>Copies through a temporary name, so an interrupted copy never leaves a half-written game file under its real name.</summary>
    internal static async Task CopyAsync(string source, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = dest + ".mele-restore";
        try
        {
            await using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
                await src.CopyToAsync(dst, 1 << 20, ct);
            var info = new FileInfo(source);
            if (new FileInfo(part).Length != info.Length) throw new IOException("the copy has a different size from the original");
            File.SetLastWriteTimeUtc(part, info.LastWriteTimeUtc);
            if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);
            File.Move(part, dest, overwrite: true);
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }
}
