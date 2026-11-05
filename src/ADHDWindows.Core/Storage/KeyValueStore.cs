namespace ADHDWindows.Core.Storage;

/// <summary>Хранилище значений по ключу. Значения сериализуются в JSON.</summary>
public interface IKeyValueStore
{
    T? Get<T>(string key, T? fallback = default);
    void Set<T>(string key, T value);
    bool Remove(string key);
}

/// <summary>Часть хранилища с общим префиксом, чтобы ключи модулей не пересекались.</summary>
public sealed class ScopedStore(IKeyValueStore inner, string prefix) : IKeyValueStore
{
    public T? Get<T>(string key, T? fallback = default) => inner.Get(Scoped(key), fallback);

    public void Set<T>(string key, T value) => inner.Set(Scoped(key), value);

    public bool Remove(string key) => inner.Remove(Scoped(key));

    private string Scoped(string key) => $"{prefix}/{key}";
}
