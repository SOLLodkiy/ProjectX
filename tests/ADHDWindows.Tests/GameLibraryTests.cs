using ADHDWindows.Core.Data;
using ADHDWindows.Core.Locks;
using ADHDWindows.Modules.Locks;
using ADHDWindows.Modules.Locks.Games;
using Xunit.Abstractions;

namespace ADHDWindows.Tests;

public sealed class GameLibraryTests(ITestOutputHelper output) : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;

    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly ManualClock _clock = new();

    private string Steam => _temp.Combine("Steam");

    private string Common => Path.Combine(Steam, "steamapps", "common");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Vdf_reads_nested_blocks_escapes_and_comments()
    {
        var root = Vdf.Parse("""
            // комментарий
            "libraryfolders"
            {
                "0" { "path" "D:\\Games\\Steam" "apps" { "570" "123" } }
                "1" "E:\\Old"
            }
            """);

        var folders = root["libraryfolders"]!;
        Assert.Equal(@"D:\Games\Steam", folders["0"]!.Text("path"));
        Assert.Equal("123", folders["0"]!["apps"]!.Text("570"));
        Assert.Equal(@"E:\Old", folders.Text("1"));
    }

    [Fact]
    public void Vdf_rejects_a_broken_file()
    {
        Assert.Throws<FormatException>(() => Vdf.Parse("\"AppState\" { \"appid\" \"1\""));
        Assert.Throws<FormatException>(() => Vdf.Parse("\"AppState\" { \"name\" \"оборвано"));
    }

    [Fact]
    public void Steam_games_are_found_in_every_library()
    {
        var second = _temp.Combine("Library2");
        WriteSteamLibraries(second);
        WriteSteamGame(Steam, "570", "Dota 2", "dota 2 beta", 40 * Gb);
        WriteSteamGame(second, "413150", "Stardew Valley", "Stardew Valley", Gb);
        // Сломанная запись не мешает остальным.
        System.IO.File.WriteAllText(Path.Combine(Steam, "steamapps", "appmanifest_1.acf"), "\"AppState\" {");

        var source = new SteamSource(Steam, _log);
        var games = source.Read().OrderBy(game => game.GameId).ToList();

        Assert.Equal(2, games.Count);
        Assert.Equal(new InstalledGame("steam", "413150", "Stardew Valley", Path.Combine(second, "steamapps", "common", "Stardew Valley"), Gb), games[0]);
        Assert.Equal("game:steam:570", games[1].Key);
        Assert.Contains(Path.Combine(second, "steamapps", "common"), source.LibraryRoots());
    }

    [Fact]
    public void Missing_steam_gives_no_games()
    {
        var source = new SteamSource(_temp.Combine("nothing"), _log);

        Assert.Empty(source.Read());
        Assert.Empty(source.LibraryRoots());
    }

    [Fact]
    public void Epic_games_are_read_and_addons_and_tools_are_skipped()
    {
        var manifests = _temp.Combine("Manifests");
        Directory.CreateDirectory(manifests);
        WriteEpic(manifests, "a", """{ "AppName": "Fortnite", "DisplayName": "Fortnite", "InstallLocation": "D:\\Epic\\Fortnite", "InstallSize": 100, "AppCategories": ["games", "applications"] }""");
        WriteEpic(manifests, "b", """{ "AppName": "FortniteDlc", "MainGameAppName": "Fortnite", "InstallLocation": "D:\\Epic\\Fortnite", "AppCategories": ["addons"] }""");
        WriteEpic(manifests, "c", """{ "AppName": "UE_5.4", "InstallLocation": "D:\\Epic\\UE_5.4", "AppCategories": ["engines"] }""");
        WriteEpic(manifests, "d", "не json");

        var game = Assert.Single(new EpicSource(manifests, _log).Read());

        Assert.Equal(new InstalledGame("epic", "Fortnite", "Fortnite", @"D:\Epic\Fortnite", 100), game);
    }

    [Fact]
    public void Real_libraries_of_this_computer_are_read_without_errors()
    {
        var sources = new IGameSource[]
        {
            new SteamSource(null, _log),
            new EpicSource(null, _log),
            RegistrySource.Gog(_log),
            RegistrySource.Ubisoft(_log),
        };
        var library = new GameLibrary(sources, Catalog(), _log, _clock);

        library.Refresh();

        foreach (var game in library.Games)
        {
            output.WriteLine($"{game.Launcher} {game.GameId} {game.Name}: {library.Offer(game).Price}");
            Assert.False(string.IsNullOrWhiteSpace(game.Name));
        }

        Assert.Empty(_log.Warnings);
        Assert.Empty(_log.Errors);
    }

    [Theory]
    [InlineData(1, 1500)]
    [InlineData(10, 2500)]
    [InlineData(50, 4000)]
    public void Game_outside_the_catalog_is_priced_by_size(long size, long price)
    {
        var game = new InstalledGame("steam", "1", "Indie", @"D:\Games\Indie", size * Gb);

        Assert.Equal(price, Catalog().PriceOf(game, game.Size));
    }

    [Fact]
    public void Catalog_price_wins_and_is_shared_by_launchers()
    {
        var catalog = Catalog();

        Assert.Equal(6000, catalog.PriceOf(new InstalledGame("steam", "1091500", "x", @"D:\x", 1), 1));
        Assert.Equal(6000, catalog.PriceOf(new InstalledGame("gog", "1423049311", "x", @"D:\x"), null));
    }

    [Fact]
    public void Size_is_measured_when_the_launcher_does_not_know_it_and_default_is_used_without_folder()
    {
        var folder = _temp.Combine("Gog", "Tiny");
        Directory.CreateDirectory(folder);
        System.IO.File.WriteAllBytes(Path.Combine(folder, "game.exe"), new byte[1024]);
        var tiny = new InstalledGame("gog", "1", "Tiny", folder);
        var lost = new InstalledGame("gog", "2", "Lost", _temp.Combine("Gog", "Lost"));
        var library = Library(new FakeGameSource("gog", [tiny, lost]));

        Assert.Equal(1500, library.Offer(tiny).Price);
        Assert.Equal(2500, library.Offer(lost).Price);
    }

    [Fact]
    public void Every_file_inside_the_game_folder_is_the_game_and_neighbours_are_not()
    {
        var game = new InstalledGame("steam", "570", "dota", Path.Combine(Common, "Game"));
        var library = Library(new FakeGameSource("steam", [game]));

        Assert.Equal(game, library.Find(Path.Combine(Common, "Game", "bin", "win64", "game.exe")));
        Assert.Null(library.Find(Path.Combine(Common, "Game2.exe")));
        Assert.Null(library.Find(_temp.Combine("Game", "game.exe")));

        var offer = library.Offer(game);
        Assert.Equal(new LockOffer("game:steam:570", "Dota 2", 5500, LockKind.Game), offer);
    }

    [Fact]
    public void Unknown_folder_in_a_steam_library_is_still_a_game()
    {
        var library = Library(new FakeGameSource("steam", [], [Common]));

        var game = library.Find(Path.Combine(Common, "Copied Game", "bin", "run.exe"));

        Assert.NotNull(game);
        Assert.Equal("game:steam:folder:copied game", game.Key);
        Assert.Null(library.Find(Path.Combine(Common, "loose.exe")));
    }

    [Fact]
    public void Ignored_steam_tools_stay_free()
    {
        var tools = new InstalledGame("steam", "228980", "Steamworks Common Redistributables", Path.Combine(Common, "Steamworks Shared"));
        var library = Library(new FakeGameSource("steam", [tools], [Common]));

        Assert.Null(library.Find(Path.Combine(Common, "Steamworks Shared", "_CommonRedist", "vcredist.exe")));
        Assert.Empty(library.Games);
    }

    [Fact]
    public void Software_from_steam_is_an_ordinary_program()
    {
        var wallpaper = new InstalledGame("steam", "431960", "Wallpaper Engine", Path.Combine(Common, "wallpaper_engine"));
        var library = Library(new FakeGameSource("steam", [wallpaper], [Common]));
        var classifier = new ProgramClassifier(ProgramCatalogFromData(), [], games: library);

        var verdict = classifier.Classify(Exe(Path.Combine(Common, "wallpaper_engine", "wallpaper64.exe"), "Wallpaper Engine Team", "Wallpaper Engine", "Wallpaper Engine"));

        Assert.Equal(LockKind.Program, verdict.Offer?.Kind);
        Assert.Equal(300, verdict.Offer?.Price);
    }

    [Fact]
    public void Folders_that_would_swallow_the_system_are_rejected()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var library = Library(new FakeGameSource(
            "gog",
            [
                new InstalledGame("gog", "1", "Root", Path.GetPathRoot(programFiles)!),
                new InstalledGame("gog", "2", "Program Files", programFiles),
                new InstalledGame("gog", "3", "Library", Steam),
            ],
            [Common]));

        Assert.Empty(library.Games);
        Assert.Null(library.Find(Path.Combine(programFiles, "App", "app.exe")));
    }

    [Fact]
    public void New_installation_is_noticed_and_recognized_right_away()
    {
        var source = new FakeGameSource("steam", [], [Common]);
        var library = Library(source);
        Assert.False(library.Refresh());

        source.Games.Add(new InstalledGame("steam", "945360", "Among Us", Path.Combine(Common, "Among Us")));
        _clock.Advance(TimeSpan.FromMinutes(1));

        // Файл запущен раньше планового перечитывания: библиотека перечитывается сама.
        Assert.Equal("game:steam:945360", library.Find(Path.Combine(Common, "Among Us", "Among Us.exe"))?.Key);
        Assert.False(library.Refresh());
        Assert.Contains(_log.Infos, entry => entry.Contains("Новая игра: Among Us", StringComparison.Ordinal));
    }

    [Fact]
    public void Removed_game_is_noticed()
    {
        var source = new FakeGameSource("gog", [new InstalledGame("gog", "1", "Game", @"D:\Games\Game")]);
        var library = Library(source);

        source.Games.Clear();

        Assert.True(library.Refresh());
        Assert.Null(library.Find(@"D:\Games\Game\game.exe"));
    }

    [Fact]
    public void Broken_source_does_not_hide_other_games()
    {
        var library = Library(new BrokenSource(), new FakeGameSource("gog", [new InstalledGame("gog", "1", "Game", @"D:\Games\Game")]));

        Assert.NotNull(library.Find(@"D:\Games\Game\game.exe"));
    }

    [Fact]
    public void Game_file_is_classified_as_a_game_not_as_its_launcher()
    {
        var game = new InstalledGame("steam", "570", "dota", Path.Combine(Common, "dota 2 beta"));
        var library = Library(new FakeGameSource("steam", [game], [Common]));
        var classifier = new ProgramClassifier(ProgramCatalogFromData(), [], games: library);

        var launcher = classifier.Classify(Exe(Path.Combine(Steam, "steam.exe"), "Valve Corporation", "Steam", "Steam"));
        var dota = classifier.Classify(Exe(Path.Combine(Common, "dota 2 beta", "game", "bin", "win64", "dota2.exe"), "Valve", "Dota 2", "Dota 2"));
        var helper = classifier.Classify(Exe(Path.Combine(Common, "dota 2 beta", "steamhelper.exe"), "Valve Corporation", "Steam", "Steam"));
        var uninstaller = classifier.Classify(Exe(Path.Combine(Common, "dota 2 beta", "unins000.exe"), null, null, "Setup/Uninstall"));
        var claude = classifier.Classify(Exe(Path.Combine(Common, "dota 2 beta", "claude.exe"), "Anthropic, PBC", "Claude", "Claude"));

        Assert.Equal("program:steam", launcher.Offer?.Key);
        Assert.Equal(new LockOffer("game:steam:570", "Dota 2", 5500, LockKind.Game), dota.Offer);
        Assert.Equal("game:steam:570", helper.Offer?.Key);
        Assert.Null(uninstaller.Offer);
        Assert.Null(claude.Offer);
    }

    [Fact]
    public void Game_catalog_file_is_valid_and_follows_the_idea()
    {
        var catalog = Catalog();

        Assert.Equal(2500, catalog.DefaultPrice);
        Assert.All(catalog.Games, entry => Assert.InRange(entry.Price, 1500, 6000));
        Assert.Equal(5500, catalog.Games.Single(entry => entry.Id == "dota2").Price);
        Assert.True(catalog.IsIgnored(new InstalledGame("steam", "228980", "", "")));
    }

    [Fact]
    public void Invalid_game_catalogs_are_rejected()
    {
        var steps = new List<SizePrice> { new(2, 1500), new(null, 4000) };
        var game = new GameEntry("a", "A", 100, [new GameRef("steam", "1")]);

        Assert.Throws<InvalidDataException>(() => new GameCatalog(0, steps, [], []).Validate());
        Assert.Throws<InvalidDataException>(() => new GameCatalog(1, [new SizePrice(2, 1)], [], []).Validate());
        Assert.Throws<InvalidDataException>(() => new GameCatalog(1, [new SizePrice(30, 1), new SizePrice(2, 1), new SizePrice(null, 1)], [], []).Validate());
        Assert.Throws<InvalidDataException>(() => new GameCatalog(1, steps, [], [game, game with { Id = "b" }]).Validate());
        Assert.Throws<InvalidDataException>(() => new GameCatalog(1, steps, [], [game with { Price = 0 }]).Validate());
        new GameCatalog(1, steps, [], [game]).Validate();
    }

    private GameLibrary Library(params IGameSource[] sources)
    {
        var library = new GameLibrary(sources, Catalog(), _log, _clock);
        library.Refresh();
        return library;
    }

    private GameCatalog Catalog() => GameCatalog.Load(LoadData());

    private ProgramCatalog ProgramCatalogFromData() => ProgramCatalog.Load(LoadData());

    private GameData LoadData()
    {
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        return data;
    }

    private static ProgramFile Exe(string path, string? company, string? product, string? description) =>
        new(path, company, product, null, description, null, false, new Lazy<string>(() => path));

    private void WriteSteamLibraries(params string[] extra)
    {
        var apps = Path.Combine(Steam, "steamapps");
        Directory.CreateDirectory(apps);
        var entries = string.Join('\n', extra.Select((path, index) =>
            $"\"{index + 1}\" {{ \"path\" \"{path.Replace(@"\", @"\\", StringComparison.Ordinal)}\" }}"));
        System.IO.File.WriteAllText(
            Path.Combine(apps, "libraryfolders.vdf"),
            $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{Steam.Replace(@"\", @"\\", StringComparison.Ordinal)}\" }} {entries} }}");
    }

    internal static void WriteSteamGame(string library, string id, string name, string folder, long size)
    {
        var apps = Path.Combine(library, "steamapps");
        Directory.CreateDirectory(Path.Combine(apps, "common", folder));
        System.IO.File.WriteAllText(
            Path.Combine(apps, $"appmanifest_{id}.acf"),
            $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{id}\"\n\t\"name\"\t\t\"{name}\"\n\t\"installdir\"\t\t\"{folder}\"\n\t\"SizeOnDisk\"\t\t\"{size}\"\n}}\n");
    }

    private static void WriteEpic(string folder, string name, string json) =>
        System.IO.File.WriteAllText(Path.Combine(folder, $"{name}.item"), json);

    private sealed class BrokenSource : IGameSource
    {
        public string Launcher => "broken";

        public IReadOnlyList<InstalledGame> Read() => throw new InvalidOperationException("сломано");

        public IReadOnlyList<string> LibraryRoots() => [];
    }
}

internal sealed class FakeGameSource(string launcher, List<InstalledGame> games, List<string>? roots = null) : IGameSource
{
    public List<InstalledGame> Games { get; } = games;

    public string Launcher => launcher;

    public IReadOnlyList<InstalledGame> Read() => [.. Games];

    public IReadOnlyList<string> LibraryRoots() => roots ?? [];
}
