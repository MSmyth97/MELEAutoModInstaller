namespace Mele.Engine.Platform;

public static class ToolFinder
{
    /// <summary>True if an executable of that name is in a PATH directory.</summary>
    public static bool OnPath(string name)
    {
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", "" } : new[] { "" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var e in exts)
                if (File.Exists(Path.Combine(dir, name + e))) return true;
        return false;
    }
}
