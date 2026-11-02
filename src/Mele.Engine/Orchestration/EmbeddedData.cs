using System.Reflection;

namespace Mele.Engine.Orchestration;

internal static class EmbeddedData
{
    public static string Read(string name)
    {
        using var s = typeof(EmbeddedData).Assembly.GetManifestResourceStream(name)
                      ?? throw new FileNotFoundException($"Embedded resource '{name}' is missing from the build");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
