namespace Mele.Engine.Platform;

public sealed class ApiKeyException(string message) : Exception(message);

/// <summary>Finds the Nexus API key without ever printing it. Order: NEXUS_API_KEY, our config file, the earlier Python tool's file.</summary>
public static class ApiKeyStore
{
    public static IEnumerable<string> CandidateFiles()
    {
        yield return Path.Combine(AppPaths.ConfigDir, "api_key");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "mele-auto-mod-installer", "api_key");
    }

    public static string Load() => Load(Environment.GetEnvironmentVariable("NEXUS_API_KEY"), CandidateFiles());

    public static string Load(string? envValue, IEnumerable<string> files)
    {
        var env = envValue?.Trim();
        if (!string.IsNullOrEmpty(env)) return env;
        foreach (var file in files.Where(File.Exists))
        {
            // A key readable by other users is a leak; refuse rather than quietly use it.
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                                                              | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new ApiKeyException($"{file} is readable by other users; run: chmod 600 \"{file}\"");
            var key = File.ReadAllText(file).Trim();
            if (key.Length > 0) return key;
        }
        throw new ApiKeyException($"No Nexus API key. Set NEXUS_API_KEY or put it in {AppPaths.ConfigDir}{Path.DirectorySeparatorChar}api_key");
    }

    /// <summary>Saves the key to our config file. On Unix the file is created owner-only BEFORE the key is written, so it is never world-readable.</summary>
    public static string Save(string key) => Save(key, CandidateFiles().First());

    public static string Save(string key, string file)
    {
        key = key.Trim();
        if (key.Length == 0) throw new ApiKeyException("The key is empty");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(file, options)) fs.Write(System.Text.Encoding.UTF8.GetBytes(key));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); // also fixes a pre-existing looser file
        return file;
    }
}
