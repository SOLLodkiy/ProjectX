using ADHDWindows.Core.Logging;

namespace ADHDWindows.Tests;

internal sealed class RecordingLog : ILog
{
    public List<string> Infos { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Errors { get; } = [];

    public void Info(string message) => Infos.Add(message);

    public void Warn(string message) => Warnings.Add(message);

    public void Error(string message, Exception? exception = null) => Errors.Add(message);
}

internal sealed class TempDirectory : IDisposable
{
    private const int DeleteAttempts = 20;

    private static readonly TimeSpan DeleteDelay = TimeSpan.FromMilliseconds(100);

    public string Path { get; } = Directory.CreateTempSubdirectory("adhdwindows-tests-").FullName;

    public string Combine(params string[] parts) =>
        System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        for (var attempt = 0; attempt < DeleteAttempts; attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                // Загруженная сборка модуля занята до конца процесса тестов.
                return;
            }
            catch (IOException)
            {
                // Сторож ещё откатывает изменения завершённой программы и пишет файл.
                Thread.Sleep(DeleteDelay);
            }
        }
    }
}
