using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mele.Engine.Platform;

/// <param name="Path">The 'Mass Effect Legendary Edition' folder (the one that contains Game).</param>
/// <param name="Source">Where it was found, for showing to the user ("Steam", "Heroic", "a folder scan").</param>
/// <param name="Games">Which of LE1, LE2, LE3 have their executable there.</param>
public sealed record DetectedInstall(string Path, string Source, IReadOnlyList<string> Games);

/// <summary>
/// Finds installs of Mass Effect Legendary Edition: the libraries of Steam, Heroic, Legendary and Epic, the Windows registry and default
/// folders, and finally a bounded look through the usual game folders (many installs, like this author's, came from an installer or a copy
/// and are in no launcher's list). Every candidate is checked on disk: a folder counts only if a game executable is really there.
/// Read-only, and it never throws for an unreadable folder.
/// </summary>
public sealed class GameLocator(IReadOnlyList<(string Source, Func<IEnumerable<string>> Candidates)> sources)
{
    private const string SteamAppId = "1328670";
    private const string DefaultFolderName = "Mass Effect Legendary Edition";
    private const int MaxFoldersPerRoot = 20_000;
    private static readonly string[] Games = ["LE1", "LE2", "LE3"];

    // ---- the checks ---------------------------------------------------------------------------------------------

    /// <summary>The games whose executable is under <paramref name="dir"/>/Game/ME{n}/Binaries/Win64. Empty = not an install.</summary>
    public static IReadOnlyList<string> GamesIn(string dir) => Games
        .Where((g, i) => Safe(() => File.Exists(Path.Combine(dir, "Game", $"ME{i + 1}", "Binaries", "Win64", $"MassEffect{i + 1}.exe"))))
        .ToList();

    /// <summary>
    /// Turns what a launcher stored into the install folder: the folder itself, the game folder (Game/ME1), the exe, or the folder that
    /// holds it. Looks up to five levels up (the exe is five below the install folder) and one level down, so launchers that record a slightly different place still count.
    /// </summary>
    public static string? Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = Path.TrimEndingDirectorySeparator(path.Trim());
        for (var i = 0; i < 6 && !string.IsNullOrEmpty(p); i++, p = Path.GetDirectoryName(p))
            if (Safe(() => Directory.Exists(p)) && GamesIn(p).Count > 0) return p;
        var start = path.Trim();
        if (Safe(() => Directory.Exists(start)))
            foreach (var child in Safe(() => Directory.EnumerateDirectories(start).ToList()) ?? [])
                if (GamesIn(child).Count > 0) return child;
        return null;
    }

    // ---- finding ------------------------------------------------------------------------------------------------

    public IReadOnlyList<DetectedInstall> Find()
    {
        var found = new List<(string Key, string Path, List<string> Sources)>();
        foreach (var (source, candidates) in sources)
        {
            foreach (var raw in Safe(() => candidates().ToList()) ?? [])
            {
                var path = Normalize(raw);
                if (path == null) continue;
                var key = Canonical(path);
                var hit = found.FirstOrDefault(f => f.Key == key);
                if (hit.Sources != null) { if (!hit.Sources.Contains(source)) hit.Sources.Add(source); }
                else found.Add((key, path, [source]));
            }
        }
        return found.Select(f => new DetectedInstall(f.Path, string.Join(", ", f.Sources), GamesIn(f.Path))).ToList();
    }

    /// <summary>The same folder reached two ways (a symlink, a different case on Windows) must count once.</summary>
    private static string Canonical(string path)
    {
        var full = Path.GetFullPath(path);
        var real = Safe(() => new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName) ?? full;
        real = Path.TrimEndingDirectorySeparator(real);
        return OperatingSystem.IsWindows() ? real.ToUpperInvariant() : real;
    }

    public static GameLocator Default() => OperatingSystem.IsWindows() ? ForWindows() : ForLinux(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    // ---- Linux --------------------------------------------------------------------------------------------------

    public static GameLocator ForLinux(string home, IEnumerable<string>? extraScanRoots = null)
    {
        var steam = new[] { ".local/share/Steam", ".steam/steam", ".steam/root", ".var/app/com.valvesoftware.Steam/.local/share/Steam" }.Select(r => Path.Combine(home, r));
        var heroic = new[] { ".config/heroic", ".var/app/com.heroicgameslauncher.hgl/config/heroic", ".config/legendary" }.Select(r => Path.Combine(home, r));
        IEnumerable<string> ScanRoots() =>
            new[] { Path.Combine(home, "Games"), Path.Combine(home, "games"), home, "/games" }
                .Concat(MountedDrives()).Concat(extraScanRoots ?? []);
        return new GameLocator(
        [
            ("Steam", () => SteamCandidates(steam)),
            ("Heroic", () => JsonPaths(heroic, ["install_path", "installPath"], maxDepth: 3)),
            ("a folder scan", () => ScanFolders(ScanRoots(), maxDepth: 3, home)),
        ]);
    }

    /// <summary>Where removable and extra drives are mounted: /mnt/NAME, /media/NAME, /media/USER/NAME and /run/media/USER/NAME.</summary>
    private static IEnumerable<string> MountedDrives()
    {
        foreach (var parent in new[] { "/mnt", "/media", "/run/media" })
            foreach (var child in Safe(() => Directory.EnumerateDirectories(parent).ToList()) ?? [])
            {
                yield return child;
                if (parent != "/mnt")
                    foreach (var mount in Safe(() => Directory.EnumerateDirectories(child).ToList()) ?? []) yield return mount;
            }
    }

    // ---- Windows ------------------------------------------------------------------------------------------------

    public static GameLocator ForWindows()
    {
        var pf = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Where(p => p.Length > 0).Distinct().ToList();
        var drives = Safe(() => DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList()) ?? [];
        var epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        return new GameLocator(
        [
            ("Steam", () => SteamCandidates(WindowsSteamRoots())),
            ("EA app or Origin", () => WindowsRegistryCandidates().Concat(pf.SelectMany(p => new[] { "EA Games", "Origin Games", "EA Desktop" }.Select(f => Path.Combine(p, f, DefaultFolderName))))),
            ("Epic Games", () => JsonPaths([epic], ["InstallLocation"], maxDepth: 1).Concat(pf.Select(p => Path.Combine(p, "Epic Games", "MassEffectLegendaryEdition")))),
            ("a folder scan", () => ScanFolders(drives.SelectMany(d => new[] { Path.Combine(d, "Games"), Path.Combine(d, "Program Files"), Path.Combine(d, "Program Files (x86)") }), maxDepth: 3, null)),
        ]);
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> WindowsRegistryCandidates()
    {
        foreach (var key in new[] { @"SOFTWARE\WOW6432Node\BioWare\Mass Effect Legendary Edition", @"SOFTWARE\BioWare\Mass Effect Legendary Edition" })
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
            foreach (var name in new[] { "Install Dir", "InstallDir", "InstallLocation" })
                if (k?.GetValue(name) is string s && s.Length > 0) yield return s;
        }
    }

    private static IEnumerable<string> WindowsSteamRoots()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (k?.GetValue("SteamPath") is string s && s.Length > 0) yield return s;
        foreach (var d in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(p => p.Length > 0)) yield return Path.Combine(d, "Steam");
    }

    // ---- the sources --------------------------------------------------------------------------------------------

    /// <summary>Steam: every library folder from libraryfolders.vdf, and the install folder named in the game's app manifest.</summary>
    public static IEnumerable<string> SteamCandidates(IEnumerable<string> steamRoots)
    {
        foreach (var root in steamRoots.Distinct())
        {
            var libs = new List<string> { root };
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (Safe(() => File.Exists(vdf)))
                foreach (Match m in Regex.Matches(Safe(() => File.ReadAllText(vdf)) ?? "", "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            foreach (var lib in libs.Distinct())
            {
                var steamapps = Path.Combine(lib, "steamapps");
                var manifest = Path.Combine(steamapps, $"appmanifest_{SteamAppId}.acf");
                var dirName = Safe(() => File.Exists(manifest)) ? Regex.Match(Safe(() => File.ReadAllText(manifest)) ?? "", "\"installdir\"\\s+\"([^\"]+)\"") is { Success: true } m ? m.Groups[1].Value : DefaultFolderName : DefaultFolderName;
                yield return Path.Combine(steamapps, "common", dirName);
            }
        }
    }

    /// <summary>Launcher files such as Heroic's and Legendary's installed.json: collects every value stored under the given key names, however the file is nested.</summary>
    public static IEnumerable<string> JsonPaths(IEnumerable<string> roots, string[] keys, int maxDepth)
    {
        foreach (var root in roots)
        {
            if (!Safe(() => Directory.Exists(root))) continue;
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = maxDepth, IgnoreInaccessible = true };
            foreach (var file in Safe(() => Directory.EnumerateFiles(root, "*.*", options).Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".item", StringComparison.OrdinalIgnoreCase)).Take(2000).ToList()) ?? [])
            {
                if (new FileInfo(file).Length > 20_000_000) continue;
                var text = Safe(() => File.ReadAllText(file));
                if (text == null || text.IndexOf("Mass", StringComparison.OrdinalIgnoreCase) < 0 && !keys.Any(k => text.Contains(k))) continue;
                JsonDocument? doc = null;
                try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
                catch (JsonException) { continue; }
                using (doc)
                    foreach (var v in Strings(doc.RootElement, keys)) yield return v;
            }
        }
    }

    private static IEnumerable<string> Strings(JsonElement e, string[] keys)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String && keys.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && p.Value.GetString() is { Length: > 0 } s) yield return s;
                    else foreach (var inner in Strings(p.Value, keys)) yield return inner;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) foreach (var inner in Strings(item, keys)) yield return inner;
                break;
        }
    }

    /// <summary>
    /// A bounded look through the usual game folders, at most <paramref name="maxDepth"/> levels down, skipping hidden folders. The home
    /// folder itself is only listed one level deep so a large home is never walked.
    /// </summary>
    public static IEnumerable<string> ScanFolders(IEnumerable<string> roots, int maxDepth, string? shallowRoot)
    {
        foreach (var root in roots.Distinct())
        {
            if (!Safe(() => Directory.Exists(root))) continue;
            var depth = shallowRoot != null && string.Equals(root, shallowRoot, StringComparison.Ordinal) ? 1 : maxDepth;
            var pending = new Queue<(string Dir, int Depth)>();
            pending.Enqueue((root, 0));
            for (var visited = 0; pending.Count > 0 && visited < MaxFoldersPerRoot; visited++)
            {
                var (dir, d) = pending.Dequeue();
                if (d > 0 && GamesIn(dir).Count > 0) { yield return dir; continue; }
                if (d >= depth) continue;
                foreach (var sub in Safe(() => Directory.EnumerateDirectories(dir).ToList()) ?? [])
                    if (!Path.GetFileName(sub).StartsWith('.')) pending.Enqueue((sub, d + 1));
            }
        }
    }

    private static T? Safe<T>(Func<T> f)
    {
        try { return f(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return default; }
    }
}
