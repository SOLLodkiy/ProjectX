using ADHDWindows.Core.Data;

namespace ADHDWindows.Tests;

public sealed class GameDataTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();

    public void Dispose() => _temp.Dispose();

    private void WriteFile(string relativePath, string content)
    {
        var path = _temp.Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private GameData LoadData()
    {
        var data = new GameData(_temp.Path, _log);
        data.Load();
        return data;
    }

    [Fact]
    public void Files_are_available_by_relative_path()
    {
        WriteFile("prices/sites.json", """{ "youtube": 100 }""");

        Assert.True(LoadData().TryGet<Dictionary<string, int>>("prices/sites", out var prices));
        Assert.Equal(100, prices!["youtube"]);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed()
    {
        WriteFile("items.json", """
            // Комментарий автора контента
            [1, 2,]
            """);

        Assert.True(LoadData().TryGet<int[]>("items", out var items));
        Assert.Equal([1, 2], items!);
    }

    [Fact]
    public void Broken_file_is_skipped_without_affecting_others()
    {
        WriteFile("broken.json", "{");
        WriteFile("good.json", "1");

        var data = LoadData();

        Assert.Equal(["good"], data.Keys);
        Assert.Single(_log.Errors);
    }

    [Fact]
    public void Missing_directory_gives_empty_data()
    {
        var data = new GameData(_temp.Combine("missing"), _log);

        data.Load();

        Assert.Empty(data.Keys);
    }

    [Fact]
    public void Texts_fall_back_to_the_key_and_the_default_language()
    {
        WriteFile("texts/ru.json", """{ "greeting": "Привет, {0}" }""");
        var texts = new TextCatalog(LoadData(), "ru", _log);

        texts.Use("fr");

        Assert.Equal("Привет, игрок", texts.Get("greeting", "игрок"));
        Assert.Equal("unknown.key", texts.Get("unknown.key"));
        Assert.Single(_log.Warnings);
    }

    [Fact]
    public void Project_texts_contain_everything_the_tray_needs()
    {
        var repository = RepositoryRoot.Find();
        var data = new GameData(Path.Combine(repository, "data"), _log);
        data.Load();

        Assert.True(data.TryGet<Dictionary<string, string>>("texts/ru", out var texts));
        Assert.Contains("app.title", texts!.Keys);
        Assert.Contains("tray.exit", texts.Keys);
        Assert.Contains("tray.devMode", texts.Keys);
    }
}
