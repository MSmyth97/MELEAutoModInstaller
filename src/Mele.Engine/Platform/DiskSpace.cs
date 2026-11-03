namespace Mele.Engine.Platform;

public static class DiskSpace
{
    /// <summary>Free space in GB on the drive holding <paramref name="dir"/> (or its nearest existing parent); null when it can't be read.</summary>
    public static double? FreeGb(string dir)
    {
        try
        {
            var probe = dir;
            while (!Directory.Exists(probe) && Path.GetDirectoryName(probe) is { Length: > 0 } up) probe = up;
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(probe))!).AvailableFreeSpace / 1073741824.0;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }
}
