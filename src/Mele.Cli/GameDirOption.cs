using Mele.Engine.Platform;

namespace Mele.Cli;

internal static class GameDirOption
{
    /// <summary>--game-dir if given; otherwise the single Legendary Edition install found on this machine. Several or none is an error that says what to do.</summary>
    public static string GameDir(this Opts o)
    {
        if (o.Get("game-dir") is { } given) return given;
        var found = GameLocator.Default().Find();
        switch (found.Count)
        {
            case 1:
                Console.WriteLine($"Using the game folder found in {found[0].Source}: {found[0].Path}");
                return found[0].Path;
            case 0:
                throw new ArgumentException("--game-dir (the 'Mass Effect Legendary Edition' folder) was not given and no install was found. Run 'mele detect' to see where it looked.");
            default:
                throw new ArgumentException("--game-dir was not given and several installs were found; pick one with --game-dir:\n" + string.Join("\n", found.Select(f => $"  {f.Path}  ({f.Source})")));
        }
    }
}
