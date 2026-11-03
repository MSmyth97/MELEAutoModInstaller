using Mele.Engine.Platform;

namespace Mele.Tests;

public class GameLocatorTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _home;
    public GameLocatorTests() => _home = Directory.CreateDirectory(System.IO.Path.Combine(_tmp.Path, "home")).FullName;
    public void Dispose() => _tmp.Dispose();

    private string P(params string[] parts) => System.IO.Path.Combine([_tmp.Path, .. parts]);

    /// <summary>Makes a folder that looks like an install (an exe for each of the given games) and returns it.</summary>
    private static string Install(string dir, params int[] games)
    {
        foreach (var n in games.Length > 0 ? games : [1, 2, 3])
        {
            var exe = System.IO.Path.Combine(dir, "Game", $"ME{n}", "Binaries", "Win64", $"MassEffect{n}.exe");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, "exe");
        }
        return dir;
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void An_install_is_recognised_by_its_executables_and_lists_which_games_it_has()
    {
        Assert.Equal(["LE1", "LE2", "LE3"], GameLocator.GamesIn(Install(P("a"))));
        Assert.Equal(["LE1", "LE3"], GameLocator.GamesIn(Install(P("b"), 1, 3)));
        Directory.CreateDirectory(P("c", "Game", "ME1"));                                    // folders without the exe do not count
        Assert.Empty(GameLocator.GamesIn(P("c")));
        Assert.Empty(GameLocator.GamesIn(P("does-not-exist")));
    }

    [Fact]
    public void Whatever_a_launcher_stored_is_turned_into_the_install_folder()
    {
        var root = Install(P("Mass Effect Legendary Edition"));
        void Is(string? expected, string input, string what) => Assert.True(expected == GameLocator.Normalize(input), $"{what}: expected '{expected}' but got '{GameLocator.Normalize(input)}' for '{input}'");

        Is(root, root, "the folder itself");
        Is(root, root + System.IO.Path.DirectorySeparatorChar, "with a trailing separator");
        Is(root, System.IO.Path.Combine(root, "Game", "ME1"), "the game folder");
        Is(root, System.IO.Path.Combine(root, "Game", "ME2", "Binaries", "Win64", "MassEffect2.exe"), "the exe");
        Is(null, P("nothing-here"), "a folder that does not exist");
        Is(null, "", "an empty string");

        var library = Directory.CreateDirectory(P("Library")).FullName;                                                // a launcher that records the parent folder
        var inner = Install(System.IO.Path.Combine(library, "Mass Effect Legendary Edition"));
        Is(inner, library, "the folder that holds the install");
    }

    [Fact]
    public void Steam_finds_the_game_in_the_main_library_and_in_extra_libraries()
    {
        var steam = P("steam");
        var second = P("big-disk", "SteamLibrary");
        Write(System.IO.Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
                "0" { "path" "{{steam.Replace("\\", "\\\\")}}" "label" "" }
                "1" { "path" "{{second.Replace("\\", "\\\\")}}" }
            }
            """);
        var installed = Install(System.IO.Path.Combine(second, "steamapps", "common", "Mass Effect Legendary Edition"));

        var found = new GameLocator([("Steam", () => GameLocator.SteamCandidates([steam]))]).Find();
        var hit = Assert.Single(found);
        Assert.Equal(installed, hit.Path);
        Assert.Equal("Steam", hit.Source);
    }

    [Fact]
    public void Steam_uses_the_folder_name_from_the_app_manifest()
    {
        var steam = P("steam");
        Write(System.IO.Path.Combine(steam, "steamapps", "appmanifest_1328670.acf"), "\"AppState\"\n{\n\t\"appid\"\t\"1328670\"\n\t\"installdir\"\t\"ME Legendary (renamed)\"\n}\n");
        var installed = Install(System.IO.Path.Combine(steam, "steamapps", "common", "ME Legendary (renamed)"));
        Assert.Equal(installed, Assert.Single(new GameLocator([("Steam", () => GameLocator.SteamCandidates([steam]))]).Find()).Path);
    }

    [Fact]
    public void Steam_with_no_game_or_no_library_file_finds_nothing_and_does_not_throw()
    {
        Directory.CreateDirectory(P("steam", "steamapps"));
        Assert.Empty(new GameLocator([("Steam", () => GameLocator.SteamCandidates([P("steam"), P("no-steam-here")]))]).Find());
    }

    [Fact]
    public void Launcher_json_files_are_searched_whatever_their_nesting()
    {
        var installed = Install(P("heroic-games", "Mass Effect Legendary Edition"));
        Write(P("config", "legendaryConfig", "legendary", "installed.json"),
            $$"""{ "Abc": { "app_name": "Abc", "title": "Mass Effect (TM) Legendary Edition", "install_path": "{{installed.Replace("\\", "\\\\")}}" } }""");
        Write(P("config", "gog_store", "installed.json"), """{ "installed": [ { "appName": "1", "install_path": "/somewhere/Fallout London" } ] }""");   // another game: ignored
        Write(P("config", "broken.json"), "{ not json ");                                                                                                       // unreadable file: ignored

        var found = new GameLocator([("Heroic", () => GameLocator.JsonPaths([P("config")], ["install_path"], 3))]).Find();
        Assert.Equal(installed, Assert.Single(found).Path);
    }

    [Fact]
    public void Epic_manifests_are_read_for_their_install_location()
    {
        var installed = Install(P("Epic Games", "MassEffectLegendaryEdition"));
        Write(P("manifests", "ABC123.item"), $$"""{ "DisplayName": "Mass Effect Legendary Edition", "InstallLocation": "{{installed.Replace("\\", "\\\\")}}" }""");
        Assert.Equal(installed, Assert.Single(new GameLocator([("Epic Games", () => GameLocator.JsonPaths([P("manifests")], ["InstallLocation"], 1))]).Find()).Path);
    }

    [Fact]
    public void The_scan_finds_installs_that_no_launcher_knows_about_and_stays_within_its_depth()
    {
        var shallow = Install(P("Games", "Mass Effect Legendary Edition"));
        var renamed = Install(P("Games", "backup-copies", "ME-LE"));                                           // renamed, one folder deeper
        Install(P("Games", "a", "b", "c", "too-deep"));                                                         // four levels down: out of range
        Install(P("Games", ".hidden", "Mass Effect Legendary Edition"));                                        // hidden folders are skipped

        var found = GameLocator.ScanFolders([P("Games")], maxDepth: 3, shallowRoot: null).ToList();
        Assert.Equivalent(new[] { shallow, renamed }, found);
    }

    [Fact]
    public void The_scan_does_not_walk_into_an_install_it_has_found_and_only_lists_the_home_folder_one_level_deep()
    {
        var install = Install(P("Games", "ME"));
        Install(System.IO.Path.Combine(install, "Game", "nested", "ME"));                                       // inside an install: not reported separately
        Assert.Equal([install], GameLocator.ScanFolders([P("Games")], 3, null).ToList());

        Install(System.IO.Path.Combine(_home, "Documents", "stuff", "ME"));                                     // two down in home: not looked at
        var topLevel = Install(System.IO.Path.Combine(_home, "MassEffectLE"));
        Assert.Equal([topLevel], GameLocator.ScanFolders([_home], 3, _home).ToList());
    }

    [Fact]
    public void The_same_folder_found_twice_is_reported_once_with_both_sources()
    {
        var root = Install(P("Games", "Mass Effect Legendary Edition"));
        var finder = new GameLocator([("Steam", () => [root]), ("a folder scan", () => [System.IO.Path.Combine(root, "Game", "ME1"), root + "/"])]);
        var hit = Assert.Single(finder.Find());
        Assert.Equal("Steam, a folder scan", hit.Source);
    }

    [Fact]
    public void A_symlink_to_an_install_is_not_listed_as_a_second_install()
    {
        if (OperatingSystem.IsWindows()) return;
        var real = Install(P("Games", "Mass Effect Legendary Edition"));
        var link = P("Shortcuts", "ME");
        Directory.CreateDirectory(P("Shortcuts"));
        Directory.CreateSymbolicLink(link, real);
        Assert.Single(new GameLocator([("a", () => [real]), ("b", () => [link])]).Find());
    }

    [Fact]
    public void Two_different_installs_are_both_reported_and_bad_candidates_are_dropped()
    {
        var one = Install(P("Games", "one"));
        var two = Install(P("Other", "two"), 1);
        var found = new GameLocator([("x", () => [one, two, P("missing"), "", "   "])]).Find();
        Assert.Equal([one, two], found.Select(f => f.Path));
        Assert.Equal([["LE1", "LE2", "LE3"], ["LE1"]], found.Select(f => (IReadOnlyList<string>)f.Games).ToArray());
    }

    [Fact]
    public void A_source_that_throws_an_unreadable_folder_error_does_not_break_the_others()
    {
        var ok = Install(P("Games", "ok"));
        var finder = new GameLocator([("bad", () => throw new UnauthorizedAccessException("denied")), ("good", () => [ok])]);
        Assert.Equal(ok, Assert.Single(finder.Find()).Path);
    }

    [Fact]
    public void On_linux_a_home_with_a_games_folder_is_found_and_an_empty_home_finds_nothing()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Empty(GameLocator.ForLinux(_home).Find().Where(f => f.Path.StartsWith(_home)));
        var install = Install(System.IO.Path.Combine(_home, "Games", "Mass Effect Legendary Edition"));
        var hit = Assert.Single(GameLocator.ForLinux(_home).Find(), f => f.Path == install);
        Assert.Equal("a folder scan", hit.Source);
    }

    [Fact]
    public void On_linux_steam_in_the_usual_place_is_found_first_by_source_name()
    {
        if (OperatingSystem.IsWindows()) return;
        var install = Install(System.IO.Path.Combine(_home, ".local", "share", "Steam", "steamapps", "common", "Mass Effect Legendary Edition"));
        Assert.Equal("Steam", Assert.Single(GameLocator.ForLinux(_home).Find(), f => f.Path == install).Source);
    }
}
