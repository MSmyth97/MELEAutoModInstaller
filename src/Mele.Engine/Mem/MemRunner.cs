using System.Text.RegularExpressions;

namespace Mele.Engine.Mem;

public sealed record MemResult(int ExitCode, IReadOnlyList<string> Lines)
{
    /// <summary>MEM exits 0 on failure paths too in places, so also require its completion line.</summary>
    public bool Success => ExitCode == 0 && Lines.Any(l => l.Contains("Installation finished", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Drives MassEffectModderNoGui. Game ids: 1 = LE1, 2 = LE2, 3 = LE3 (this is the Legendary-Edition-only build).</summary>
public sealed class MemRunner(IProcessRunner runner, string memExe, IReadOnlyDictionary<string, string>? env = null)
{
    public static int GameId(string game) => game switch { "LE1" => 1, "LE2" => 2, "LE3" => 3, _ => throw new ArgumentException($"Unknown game '{game}'") };

    public async Task<int?> VersionAsync(CancellationToken ct = default)
    {
        var lines = new List<string>();
        int rc;
        try { rc = await runner.RunAsync(memExe, ["--version"], lines.Add, env, ct); }
        catch (System.ComponentModel.Win32Exception) { return null; }
        var m = lines.Select(l => Regex.Match(l, @"^\s*Version:\s*(\d+)")).FirstOrDefault(x => x.Success);
        return rc == 0 && m != null ? int.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>Records where the game lives in MEM's own config file (does not touch the game).</summary>
    public async Task<bool> SetGamePathAsync(string game, string gameRoot, CancellationToken ct = default) =>
        await runner.RunAsync(memExe, ["--set-game-data-path", "--gameid", GameId(game).ToString(), "--path", gameRoot], null, env, ct) == 0;

    /// <summary>
    /// Installs the .mem files listed in <paramref name="mflFile"/>, in order (same command line M3 uses).
    /// THIS MODIFIES THE GAME. Callers must have verified backups first.
    /// </summary>
    public async Task<MemResult> InstallAsync(string game, string mflFile, Action<string>? onLine = null, bool skipMarkers = false, CancellationToken ct = default)
    {
        var args = new List<string> { "--install-mods", "--gameid", GameId(game).ToString(), "--input", mflFile, "--verify" };
        if (skipMarkers) args.Add("--skip-markers");
        var lines = new List<string>();
        void Collect(string l) { lock (lines) lines.Add(l); onLine?.Invoke(l); }
        var rc = await runner.RunAsync(memExe, args, Collect, env, ct);
        return new MemResult(rc, lines);
    }
}
