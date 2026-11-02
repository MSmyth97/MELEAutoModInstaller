using System.Text;
using System.Text.RegularExpressions;

namespace Mele.Engine.Platform;

/// <summary>
/// A Wine/Proton prefix on disk: reads registry values from <c>user.reg</c> and converts between Windows and
/// Linux paths using the prefix's <c>dosdevices</c> drive links. This is how a native Linux process sees what
/// M3 (running inside the prefix) recorded.
/// </summary>
public sealed class WinePrefix(string root)
{
    public string Root { get; } = root;

    /// <summary>Accepts the prefix itself or its parent (Steam's compatdata/ID folder holds the prefix in "pfx").</summary>
    public static WinePrefix? Find(string path)
    {
        foreach (var candidate in new[] { path, Path.Combine(path, "pfx") })
            if (File.Exists(Path.Combine(candidate, "user.reg"))) return new WinePrefix(candidate);
        return null;
    }

    /// <summary>Reads a string value from HKEY_CURRENT_USER (user.reg). <paramref name="key"/> uses single backslashes.</summary>
    public string? ReadUserString(string key, string valueName)
    {
        var reg = Path.Combine(Root, "user.reg");
        if (!File.Exists(reg)) return null;
        var header = "[" + key.Replace("\\", "\\\\") + "]";
        bool inSection = false;
        foreach (var raw in File.ReadLines(reg, Encoding.UTF8))
        {
            if (raw.StartsWith('['))
            {
                inSection = raw.StartsWith(header, StringComparison.OrdinalIgnoreCase)
                            && (raw.Length == header.Length || raw[header.Length] == ' ');
                continue;
            }
            if (!inSection) continue;
            var m = Regex.Match(raw, "^\"(?<n>(?:[^\"\\\\]|\\\\.)*)\"=\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"\\s*$");
            if (m.Success && string.Equals(Unescape(m.Groups["n"].Value), valueName, StringComparison.OrdinalIgnoreCase))
                return Unescape(m.Groups["v"].Value);
        }
        return null;
    }

    /// <summary>"X:\Games\ME1 Backup" -> "/home/user/Games/ME1 Backup" through the prefix's drive mapping; null if the drive isn't mapped.</summary>
    public string? ToLinuxPath(string windowsPath)
    {
        var m = Regex.Match(windowsPath, @"^(?<d>[A-Za-z]):[\\/]?(?<rest>.*)$");
        if (!m.Success) return null;
        var letter = char.ToLowerInvariant(m.Groups["d"].Value[0]);
        var drive = ResolveDrive(letter);
        if (drive == null && letter == 'c') drive = Path.Combine(Root, "drive_c");
        if (drive == null) return null;
        var rest = m.Groups["rest"].Value.Replace('\\', '/');
        return rest.Length == 0 ? drive : Path.Combine(drive, rest);
    }

    /// <summary>Linux path -> the path Wine programs see (via the z: drive, which maps "/").</summary>
    public string? ToWindowsPath(string linuxPath)
    {
        if (ResolveDrive('z') != "/") return null;
        return "Z:" + Path.GetFullPath(linuxPath).Replace('/', '\\');
    }

    /// <summary>Target of dosdevices/&lt;letter&gt;: (a symlink), or null when that drive isn't mapped.</summary>
    private string? ResolveDrive(char letter)
    {
        var link = new FileInfo(Path.Combine(Root, "dosdevices", letter + ":"));
        return link.LinkTarget != null ? link.ResolveLinkTarget(returnFinalTarget: true)?.FullName : null;
    }

    private static string Unescape(string s) => Regex.Replace(s, @"\\(.)", "$1");
}
