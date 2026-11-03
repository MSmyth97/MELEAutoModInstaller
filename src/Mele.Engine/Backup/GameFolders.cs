using Mele.Engine.Mem;

namespace Mele.Engine.Backup;

public static class GameFolders
{
    /// <summary>The folder Mod Manager treats as a game: <c>{GameDir}/Game/ME1</c> for LE1, and so on. A backup mirrors its contents.</summary>
    public static string Root(string gameDir, string game) => Path.Combine(gameDir, "Game", $"ME{MemRunner.GameId(game)}");
}
