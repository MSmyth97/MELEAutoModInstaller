using System.Security.Cryptography;
using Mele.Engine.Archive;
using Mele.Engine.Biq;

namespace Mele.Engine.Stage;

public enum StageStatus
{
    Ok,
    /// <summary>Staged, but the archive's moddesc.ini differs from the one the group file recorded (mod was updated).</summary>
    OkVersionDiffers,
}

public sealed class StageException(string message) : Exception(message);

/// <summary>Extracts a mod archive and places the right mod folder into an ME3Tweaks Mod Manager mod library.</summary>
public sealed class Stager(IArchiveExtractor extractor)
{
    /// <summary>True when the library already holds this mod with the recorded moddesc.ini hash.</summary>
    public static bool IsStaged(string library, ModEntry entry)
    {
        var ini = Path.Combine(library, entry.ModDescRelPath);
        return entry.ModDescMd5.Length > 0 && File.Exists(ini) && Md5(ini) == entry.ModDescMd5;
    }

    public async Task<StageStatus> StageAsync(string archive, string library, ModEntry entry, CancellationToken ct = default)
    {
        var libRoot = Path.GetFullPath(library);
        var dest = Path.GetFullPath(Path.Combine(libRoot, Path.GetDirectoryName(entry.ModDescRelPath)!));
        if (!dest.StartsWith(libRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new StageException($"{entry.Name}: destination escapes the library");

        // Extract on the library's own filesystem: /tmp is often a small tmpfs (a 19 GB mod overflowed one),
        // and staying on one filesystem makes the final step a rename rather than a copy.
        var scratchRoot = Path.Combine(Path.GetDirectoryName(libRoot)!, ".mele-installer-tmp");
        var scratch = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            await extractor.ExtractAsync(archive, scratch, ct);
            var inis = Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories)
                .Where(f => string.Equals(Path.GetFileName(f), "moddesc.ini", StringComparison.OrdinalIgnoreCase)).ToList();
            if (inis.Count == 0)
                throw new StageException($"{entry.Name}: no moddesc.ini in archive (nested archive or not an M3 mod)");

            var status = StageStatus.Ok;
            List<string> match;
            if (entry.ModDescMd5.Length == 0)
            {
                // No recorded hash (e.g. M3TO texture mods): accept only an unambiguous single moddesc.ini.
                match = inis.Count == 1 ? inis : [];
            }
            else
            {
                match = inis.Where(f => Md5(f) == entry.ModDescMd5).ToList();
                if (match.Count == 0)
                {
                    status = StageStatus.OkVersionDiffers;
                    var folder = Path.GetFileName(dest);
                    match = inis.Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == folder).ToList();
                    if (match.Count == 0 && inis.Count == 1) match = inis;
                }
            }
            if (match.Count == 0)
                throw new StageException($"{entry.Name}: {inis.Count} moddesc.ini files in archive, none match the group");

            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            MoveDirectory(Path.GetDirectoryName(match[0])!, dest);
            return status;
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }

    private static void MoveDirectory(string from, string to)
    {
        try { Directory.Move(from, to); }
        catch (IOException) // different volume
        {
            CopyDirectory(from, to);
            Directory.Delete(from, recursive: true);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }

    internal static string Md5(string path) => Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
