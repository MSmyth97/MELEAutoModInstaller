using Mele.Engine.Archive;
using Mele.Engine.Stage;

namespace Mele.Engine.Textures;

/// <summary>Extracts a .mem mod archive to a folder and writes the ordered file list MEM installs from.</summary>
public static class MemFiles
{
    private static readonly string[] TextureExtensions = [".mem", ".tpf", ".mod"];

    /// <summary>Folder for a .mem item. The index is the item's 1-based position in the game's list (m3to items count).</summary>
    public static string ItemDir(string memRoot, string game, int index, TextureItem item) =>
        Path.Combine(memRoot, game, $"{index:00} {item.Name}");

    public static async Task<IReadOnlyList<string>> ExtractAsync(IArchiveExtractor extractor, string archive, string destDir,
        IReadOnlyList<string>? select, CancellationToken ct = default)
    {
        var scratch = Path.Combine(Path.GetDirectoryName(destDir)!, ".extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            await extractor.ExtractAsync(archive, scratch, ct);
            var found = Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories)
                .Where(f => TextureExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToList();
            if (select != null)
            {
                var want = new HashSet<string>(select, StringComparer.OrdinalIgnoreCase);
                var missing = want.Except(found.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase).ToList();
                if (missing.Count > 0) throw new StageException($"Missing selected files in archive: {string.Join(", ", missing)}");
                found = found.Where(f => want.Contains(Path.GetFileName(f))).ToList();
            }
            if (found.Count == 0) throw new StageException("No .mem files found in archive");

            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
            Directory.CreateDirectory(destDir);
            foreach (var f in found) File.Move(f, Path.Combine(destDir, Path.GetFileName(f)));
            return found.Select(f => Path.Combine(destDir, Path.GetFileName(f))).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally { try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } }
    }

    /// <summary>
    /// The files to install for a game, in the guide's order. A selected item installs its files in the listed order
    /// (a directory-name input to MEM would sort alphabetically instead, which is why a list file is used).
    /// </summary>
    public static IReadOnlyList<string> OrderedFiles(TextureManifest manifest, string game, string memRoot)
    {
        var files = new List<string>();
        var items = manifest.Items(game);
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Kind != TextureKind.Mem) continue;
            var dir = ItemDir(memRoot, game, i + 1, item);
            var names = item.Select ?? (IReadOnlyList<string>)Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()!;
            foreach (var n in names)
            {
                var p = Path.Combine(dir, n);
                if (!File.Exists(p)) throw new FileNotFoundException($"Expected texture file not found: {p}", p);
                files.Add(p);
            }
        }
        return files;
    }

    /// <summary>Writes an .mfl (one path per line, CRLF). <paramref name="mapPath"/> converts to what MEM sees (e.g. Z:\ paths under Wine).</summary>
    public static void WriteMfl(string mflPath, IEnumerable<string> files, Func<string, string>? mapPath = null)
    {
        mapPath ??= p => p;
        File.WriteAllText(mflPath, string.Concat(files.Select(f => mapPath(f) + "\r\n")), new System.Text.UTF8Encoding(false));
    }
}
