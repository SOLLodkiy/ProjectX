using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Automation;
using ADHDWindows.Core.Logging;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Keyboard;

/// <summary>
/// Решает, можно ли сейчас трогать клавиатуру. Защищены: поля паролей, экраны Windows (когда активное окно не
/// определяется или это окно входа, UAC, запроса пароля), терминалы, окно Claude, сама ADHDWindows.
/// Проверка идёт в своём потоке несколько раз в секунду, а хук клавиатуры только читает готовый ответ: хук должен
/// отвечать мгновенно. Пока ответ для нового активного окна не готов, клавиатура считается защищённой.
/// </summary>
public sealed class KeyboardGuard : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private readonly ProtectedPlaces _places;
    private readonly ILog _log;
    private readonly ConcurrentDictionary<int, (long Started, bool Protected)> _processes = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private volatile bool _protected = true;
    private IntPtr _checkedWindow;
    private int _errors;

    public KeyboardGuard(ProtectedPlaces places, ILog log)
    {
        _places = places;
        _log = log;
        Check();
        _thread = new Thread(Run) { IsBackground = true, Name = "KeyboardGuard" };
        _thread.Start();
    }

    /// <summary>Клавиатуру сейчас трогать нельзя. Для другого активного окна, чем при последней проверке, — тоже нельзя.</summary>
    public bool IsProtected => _protected || WindowControl.Foreground() != Volatile.Read(ref _checkedWindow);

    /// <summary>Подменить проверку для автотестов: так проверяется и защищённое, и обычное место без настоящих окон.</summary>
    internal Func<bool>? Override { get; set; }

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }

    /// <summary>Защищён ли процесс по имени файла или издателю.</summary>
    public bool IsProtectedProcess(int processId)
    {
        if (processId == Environment.ProcessId)
        {
            return true;
        }

        var stamp = ProcessControl.Stamp(processId);
        if (stamp is null)
        {
            return true;
        }

        if (_processes.TryGetValue(processId, out var cached) && cached.Started == stamp.Value.StartTime)
        {
            return cached.Protected;
        }

        var path = ProcessControl.ImagePath(stamp.Value);
        var result = path is null || IsProtectedFile(path);
        _processes[processId] = (stamp.Value.StartTime, result);
        return result;
    }

    /// <summary>Защищён ли файл программы по имени или издателю.</summary>
    public bool IsProtectedFile(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (_places.Processes.Any(process => string.Equals(process, name, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        try
        {
            var company = FileVersionInfo.GetVersionInfo(path).CompanyName ?? string.Empty;
            return _places.Companies.Any(mask => Matches(company, mask));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public bool IsProtectedClass(string windowClass) =>
        _places.Classes.Any(name => string.Equals(name, windowClass, StringComparison.Ordinal));

    private static bool Matches(string value, string mask) => mask.EndsWith('*')
        ? value.StartsWith(mask[..^1], StringComparison.OrdinalIgnoreCase)
        : string.Equals(value, mask, StringComparison.OrdinalIgnoreCase);

    private void Run()
    {
        while (!_stop.Token.WaitHandle.WaitOne(Interval))
        {
            Check();
        }
    }

    private void Check()
    {
        try
        {
            if (Override is { } check)
            {
                _protected = check();
                Volatile.Write(ref _checkedWindow, WindowControl.Foreground());
                return;
            }

            var target = FocusInfo.Current();
            var result = target is null
                || target.Password
                || IsProtectedClass(target.WindowClass)
                || IsProtectedProcess(target.ProcessId)
                || IsPasswordElement();
            _protected = result;
            Volatile.Write(ref _checkedWindow, target?.Window ?? IntPtr.Zero);
        }
        catch (Exception exception)
        {
            // Не удалось проверить — клавиатуру не трогаем.
            _protected = true;
            if (Interlocked.Increment(ref _errors) <= 3)
            {
                _log.Error("Не удалось проверить, куда идёт ввод", exception);
            }
        }
    }

    /// <summary>Поле пароля в браузере или программе не на Win32: его видно через UI Automation.</summary>
    private static bool IsPasswordElement()
    {
        try
        {
            return AutomationElement.FocusedElement is { } element && element.Current.IsPassword;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            // Фокус как раз переходит: на всякий случай считаем, что это пароль.
            return true;
        }
    }
}
