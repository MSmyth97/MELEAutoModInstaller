using System.Diagnostics;
using Mele.Engine.Mem;
using Mele.Engine.Platform;

namespace Mele.Engine.Provision;

/// <summary>
/// The app's own Wine prefix for ME3Tweaks Mod Manager on Linux, created and run with umu-launcher (Proton outside Steam).
/// It never touches a Steam game prefix. Native MEM does the texture work, so only M3 lives in here.
/// </summary>
public sealed class ProtonPrefix(string root, IProcessRunner runner, Func<string?>? findUmu = null)
{
    private string? Umu => (findUmu ?? FindUmu)();
    public const string ProtonPath = "GE-Proton";
    public string Root { get; } = root;

    public static string DefaultRoot => Path.Combine(AppPaths.DataDir, "prefix");

    public static string? FindUmu()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, "umu-run");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public IReadOnlyDictionary<string, string> Env => new Dictionary<string, string>
    {
        ["WINEPREFIX"] = Root,
        ["PROTONPATH"] = ProtonPath,
        ["GAMEID"] = "umu-0",
        ["WINEDEBUG"] = "-all",
    };

    public bool IsInitialized => File.Exists(Path.Combine(Root, "user.reg"));
    public WinePrefix? Wine => WinePrefix.Find(Root);

    /// <summary>The Windows user profile folder inside the prefix (Proton uses "steamuser").</summary>
    public string UserDir()
    {
        var users = Path.Combine(Root, "drive_c", "users");
        var steamuser = Path.Combine(users, "steamuser");
        if (Directory.Exists(steamuser)) return steamuser;
        return Directory.Exists(users)
            ? Directory.EnumerateDirectories(users).FirstOrDefault(d => Path.GetFileName(d) != "Public") ?? steamuser
            : steamuser;
    }

    /// <summary>Where M3 lives inside the prefix: it keeps its data and mod library next to its exe.</summary>
    public string M3Home => Path.Combine(UserDir(), "Documents", "ME3TweaksModManager");

    /// <summary>Creates the prefix (first run downloads nothing if GE-Proton and the runtime are already present).</summary>
    public async Task EnsureAsync(Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (IsInitialized) return;
        var umu = Umu ?? throw new ProvisionException("umu-launcher (umu-run) is required on Linux. Install it with your package manager (Arch: umu-launcher).");
        Directory.CreateDirectory(Root);
        var rc = await runner.RunAsync(umu, ["wineboot", "-u"], onLine, Env, ct);
        if (rc != 0 || !IsInitialized) throw new ProvisionException($"Could not create the Proton prefix at {Root} (umu-run exit {rc})");
        Directory.CreateDirectory(M3Home);
    }

    /// <summary>Starts M3 in the prefix and returns without waiting (it is a GUI the user works in).</summary>
    public Process LaunchM3(string m3Exe)
    {
        var umu = Umu ?? throw new ProvisionException("umu-run not found");
        var psi = new ProcessStartInfo(umu) { UseShellExecute = false };
        psi.ArgumentList.Add(m3Exe);
        foreach (var (k, v) in Env) psi.Environment[k] = v;
        return Process.Start(psi) ?? throw new ProvisionException("Could not start umu-run");
    }

    /// <summary>The path M3 must be given for a Linux folder (via the prefix's Z: drive, which maps "/").</summary>
    public string? WindowsPathFor(string linuxPath) => Wine?.ToWindowsPath(linuxPath);
}
