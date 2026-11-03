using Mele.Engine.Mem;
using Mele.Engine.Platform;

namespace Mele.Engine.Provision;

/// <summary>Where Mod Manager lives on this machine, and how to see its backups. Needs no network or API key.</summary>
public sealed class M3Location
{
    public ProtonPrefix? Prefix { get; private init; }
    public required string Home { get; init; }
    public required IBackupLocator Backups { get; init; }
    /// <summary>The game folders Mod Manager has registered; compares them with the one this app uses.</summary>
    public ModManagerTargets Targets { get; init; } = new(null, _ => null, _ => null, false);
    public string ExePath => Path.Combine(Home, M3Installer.ExeName);
    public string Library => Path.Combine(Home, "mods");
    public bool IsInstalled => File.Exists(ExePath);

    /// <param name="prefixDir">Linux only: a Wine/Proton prefix (or a Steam compatdata folder) to use; null/empty = the app's own prefix.</param>
    public static M3Location Resolve(string? prefixDir)
    {
        if (OperatingSystem.IsWindows())
            return new M3Location
            {
                Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ME3TweaksModManager"),
                Backups = new RegistryBackupLocator(),
                Targets = ModManagerTargets.ForWindows(),
            };
        var root = string.IsNullOrWhiteSpace(prefixDir) ? ProtonPrefix.DefaultRoot : prefixDir;
        var wine = WinePrefix.Find(root);
        // No prefix yet (first run): the folder where it WILL be, and "no backups" until M3 has made some.
        var prefix = new ProtonPrefix(wine?.Root ?? root, new ProcessRunner());
        return new M3Location
        {
            Prefix = prefix,
            Home = prefix.M3Home,
            Backups = wine == null ? new NoBackups() : new WinePrefixBackupLocator(wine),
            Targets = wine == null ? new ModManagerTargets(null, _ => null, _ => null, false) : ModManagerTargets.ForWinePrefix(wine),
        };
    }

    private sealed class NoBackups : IBackupLocator
    {
        public BackupInfo Get(string game) => new(game, null, false, "Mod Manager has not been set up yet");
    }
}
