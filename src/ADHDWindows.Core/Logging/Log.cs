namespace ADHDWindows.Core.Logging;

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

/// <summary>Журнал по дням в папке пользователя. Сбой записи журнала никогда не мешает программе.</summary>
public sealed class FileLog : ILog
{
    private const int RetentionDays = 14;

    private readonly string _directory;
    private readonly Lock _lock = new();

    public FileLog(string directory)
    {
        _directory = directory;
        try
        {
            Directory.CreateDirectory(directory);
            DeleteOldFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Без журнала программа работает так же, поэтому запуск продолжается.
        }
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
        var path = Path.Combine(_directory, $"{now:yyyy-MM-dd}.log");
        lock (_lock)
        {
            try
            {
                File.AppendAllText(path, line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Сообщать об ошибке журнала некуда.
            }
        }
    }

    private void DeleteOldFiles()
    {
        var threshold = DateTime.Now.AddDays(-RetentionDays);
        foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*.log"))
        {
            if (file.LastWriteTime < threshold)
            {
                file.Delete();
            }
        }
    }
}

/// <summary>Добавляет к сообщениям имя источника, например модуля.</summary>
public sealed class ScopedLog(ILog inner, string scope) : ILog
{
    public void Info(string message) => inner.Info($"[{scope}] {message}");

    public void Warn(string message) => inner.Warn($"[{scope}] {message}");

    public void Error(string message, Exception? exception = null) =>
        inner.Error($"[{scope}] {message}", exception);
}
