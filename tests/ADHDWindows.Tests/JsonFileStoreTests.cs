using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Tests;

public sealed class JsonFileStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly string _path;

    public JsonFileStoreTests() => _path = _temp.Combine("user", "save.json");

    public void Dispose() => _temp.Dispose();

    private JsonFileStore Open()
    {
        var store = new JsonFileStore(_path, _log);
        store.Load();
        return store;
    }

    [Fact]
    public void Values_survive_reopening()
    {
        var store = Open();
        store.Set("coins", 42);
        store.Set("profile", new Profile("Игрок", [1, 2]));
        store.Flush();

        var reopened = Open();

        Assert.Equal(42, reopened.Get<int>("coins"));
        var profile = reopened.Get<Profile>("profile");
        Assert.Equal("Игрок", profile!.Name);
        Assert.Equal([1, 2], profile.Levels);
    }

    [Fact]
    public void Missing_key_returns_fallback()
    {
        Assert.Equal(7, Open().Get("missing", 7));
    }

    [Fact]
    public void Value_of_wrong_type_returns_fallback()
    {
        var store = Open();
        store.Set("coins", "много");

        Assert.Equal(5, store.Get("coins", 5));
        Assert.Single(_log.Errors);
    }

    [Fact]
    public void Removed_key_is_gone_after_reopening()
    {
        var store = Open();
        store.Set("coins", 1);
        store.Flush();

        Assert.True(store.Remove("coins"));
        store.Flush();

        Assert.Equal(0, Open().Get<int>("coins"));
    }

    [Fact]
    public void File_keeps_cyrillic_readable()
    {
        var store = Open();
        store.Set("name", "Монетка");
        store.Flush();

        Assert.Contains("Монетка", File.ReadAllText(_path));
    }

    [Fact]
    public void Flush_without_changes_does_not_touch_file()
    {
        var store = Open();
        store.Set("coins", 1);
        store.Flush();
        var written = File.GetLastWriteTimeUtc(_path);
        File.SetLastWriteTimeUtc(_path, written.AddHours(-1));

        store.Flush();

        Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(_path));
    }

    [Fact]
    public void Corrupted_file_is_restored_from_backup_and_kept_for_inspection()
    {
        var store = Open();
        store.Set("coins", 1);
        store.Flush();
        store.Set("coins", 2);
        store.Flush();
        File.WriteAllText(_path, "{ обрыв");

        var restored = Open();

        Assert.Equal(1, restored.Get<int>("coins"));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(_path)!, "*.corrupt-*"));

        restored.Flush();
        Assert.Equal(1, Open().Get<int>("coins"));
    }

    [Fact]
    public void Corrupted_file_without_backup_starts_empty_and_keeps_the_copy()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "не json");

        var store = Open();

        Assert.Equal(0, store.Get<int>("coins"));
        var copy = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(_path)!, "*.corrupt-*"));
        Assert.Equal("не json", File.ReadAllText(copy));
    }

    [Fact]
    public void Failed_flush_keeps_changes_for_the_next_attempt()
    {
        var store = Open();
        store.Set("coins", 1);
        store.Flush();
        store.Set("coins", 2);

        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(store.Flush);
        }

        store.Flush();
        Assert.Equal(2, Open().Get<int>("coins"));
    }

    [Fact]
    public void Scoped_stores_do_not_share_keys()
    {
        var store = Open();
        var first = new ScopedStore(store, "modules/first");
        var second = new ScopedStore(store, "modules/second");

        first.Set("runs", 1);
        second.Set("runs", 2);

        Assert.Equal(1, first.Get<int>("runs"));
        Assert.Equal(2, second.Get<int>("runs"));
    }

    private sealed record Profile(string Name, int[] Levels);
}
