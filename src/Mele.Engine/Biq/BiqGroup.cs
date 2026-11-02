using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mele.Engine.Biq;

/// <summary>One mod in an ME3Tweaks Mod Manager Batch Mod Installer group (.biq2).</summary>
public sealed record ModEntry(
    string Name,
    string? GameDomain,
    int? ModId,
    int? FileId,
    string ModDescRelPath,   // forward slashes, relative to the M3 mod library, e.g. "LE1/Mod Name/moddesc.ini"
    string ModDescMd5,       // lower-case hex; empty when unknown (e.g. generated M3TO entries)
    int ModDescSize,
    string? Link)
{
    public bool Downloadable => FileId.HasValue;
}

/// <summary>A parsed .biq2 group file. These are plain JSON written by M3.</summary>
public sealed record BiqGroup(string Game, string Description, IReadOnlyList<ModEntry> Mods)
{
    private static readonly Regex LinkPattern = new(
        @"nexusmods\.com/(?<game>[^/]+)/mods/(?<mod>\d+).*[?&]file_id=(?<file>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static BiqGroup Load(string path) => Parse(File.ReadAllBytes(path));

    public static BiqGroup Parse(byte[] json)
    {
        ReadOnlySpan<byte> span = json;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF) span = span[3..];
        using var doc = JsonDocument.Parse(span.ToArray());
        var root = doc.RootElement;
        var mods = new List<ModEntry>();
        foreach (var m in root.GetProperty("mods").EnumerateArray())
        {
            var link = m.TryGetProperty("downloadlink", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            var hit = link == null ? null : LinkPattern.Match(link);
            bool ok = hit is { Success: true };
            mods.Add(new ModEntry(
                m.GetProperty("modname").GetString()!.Trim(),
                ok ? hit!.Groups["game"].Value : null,
                ok ? int.Parse(hit!.Groups["mod"].Value) : null,
                ok ? int.Parse(hit!.Groups["file"].Value) : null,
                SafeRelativePath(m.GetProperty("moddescpath").GetString()!),
                m.GetProperty("moddeschash").GetString()!.ToLowerInvariant(),
                m.GetProperty("moddescsize").GetInt32(),
                link));
        }
        return new BiqGroup(root.GetProperty("game").GetString()!,
            root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "", mods);
    }

    /// <summary>
    /// Supplies links for entries the group file has none for (matched by exact mod name). Some entries in the
    /// guide's groups (ALOV for LE2/LE3) have a null link.
    /// </summary>
    public BiqGroup WithLinks(IReadOnlyDictionary<string, string> links)
    {
        var mods = Mods.Select(m =>
        {
            if (m.Downloadable || !links.TryGetValue(m.Name, out var link)) return m;
            var hit = LinkPattern.Match(link);
            return hit.Success
                ? m with { GameDomain = hit.Groups["game"].Value, ModId = int.Parse(hit.Groups["mod"].Value), FileId = int.Parse(hit.Groups["file"].Value), Link = link }
                : m;
        }).ToList();
        return this with { Mods = mods };
    }

    internal static string SafeRelativePath(string raw)
    {
        var p = raw.Replace('\\', '/');
        if (p.StartsWith('/') || (p.Length > 1 && p[1] == ':') || p.Split('/').Contains(".."))
            throw new InvalidDataException($"Unsafe moddesc path in group file: '{raw}'");
        return p;
    }
}
