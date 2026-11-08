using ADHDWindows.Core.Hardships;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Basic;

/// <summary>
/// Одно окно: открыто может быть только одно окно программы, остальные сворачиваются и после усложнения
/// разворачиваются обратно. Окна ADHDWindows (экран покупки, HUD) и рабочий стол не считаются и не сворачиваются.
/// Окна никогда не закрываются.
/// </summary>
public sealed class OneWindowHardship : HardshipModule
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

    private readonly Lock _lock = new();
    private readonly List<MinimizedWindow> _minimized = [];
    private WindowWatcher? _watcher;
    private Timer? _timer;
    private IDisposable? _mark;
    private IntPtr _kept;
    private bool _running;

    public override HardshipInfo Info { get; } = new("oneWindow", HardshipLevel.Hard, HardshipResources.Windows);

    /// <summary>Свёрнутые усложнением окна, для проверок.</summary>
    internal IReadOnlyList<MinimizedWindow> Minimized
    {
        get
        {
            lock (_lock)
            {
                return [.. _minimized];
            }
        }
    }

    /// <summary>Настройка для проверок: трогать только окна программ из этой папки, чтобы не сворачивать окна человека.</summary>
    public const string OnlyUnderSetting = "onlyUnder";

    /// <summary>Какие окна считаются. Проверки ограничивают усложнение своими окнами.</summary>
    internal Func<WindowInfo, bool> Scope { get; set; } = _ => true;

    protected override void OnStart()
    {
        var onlyUnder = Context.Settings.Get<string>(OnlyUnderSetting);
        if (string.IsNullOrWhiteSpace(onlyUnder))
        {
            return;
        }

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(onlyUnder)) + Path.DirectorySeparatorChar;
        Context.Log.Info($"«Одно окно» ограничено папкой: {folder}");
        Scope = window => ProcessControl.Stamp(window.ProcessId) is { } stamp
            && ProcessControl.ImagePath(stamp) is { } path
            && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    protected override void Begin()
    {
        lock (_lock)
        {
            _minimized.Clear();
            _kept = IntPtr.Zero;
            _running = true;
            _mark = Context.Changes.Record(MinimizedWindowsRestorer.ChangeKind, MinimizedWindowsRestorer.ToData([]));
            Enforce();
        }

        _watcher = new WindowWatcher((_, _) => SafeEnforce(), Context.Log);
        _watcher.Start();
        _timer = new Timer(_ => SafeEnforce(), null, CheckInterval, CheckInterval);
    }

    protected override void End()
    {
        _watcher?.Dispose();
        _watcher = null;
        _timer?.Dispose();
        _timer = null;
        lock (_lock)
        {
            _running = false;
            new MinimizedWindowsRestorer().Restore(MinimizedWindowsRestorer.ToData(_minimized));
            WindowControl.TryActivate(_kept);
            Context.Log.Info($"Одно окно: развёрнуто окон {_minimized.Count}");
            _minimized.Clear();
            _mark?.Dispose();
            _mark = null;
        }
    }

    private void SafeEnforce()
    {
        try
        {
            lock (_lock)
            {
                if (_running)
                {
                    Enforce();
                }
            }
        }
        catch (Exception exception)
        {
            Context.Log.Error("Ошибка усложнения «Одно окно»", exception);
        }
    }

    /// <summary>Оставляет развёрнутым активное окно, а если активно окно ADHDWindows — прежнее.</summary>
    private void Enforce()
    {
        var windows = WindowControl.AppWindows().Where(Counts).ToList();
        var foreground = WindowControl.Foreground();
        if (windows.Exists(window => window.Handle == foreground && !window.IsMinimized))
        {
            _kept = foreground;
        }
        else if (!windows.Exists(window => window.Handle == _kept && !window.IsMinimized))
        {
            // Активного окна программы нет: остаётся верхнее из развёрнутых, окна перечисляются сверху вниз.
            _kept = windows.FirstOrDefault(window => !window.IsMinimized)?.Handle ?? IntPtr.Zero;
        }

        var extra = windows.Where(window => !window.IsMinimized && window.Handle != _kept).ToList();
        if (extra.Count == 0)
        {
            return;
        }

        foreach (var window in extra)
        {
            if (!_minimized.Exists(item => item.Handle == window.Handle.ToInt64()))
            {
                _minimized.Add(new MinimizedWindow(window.Handle.ToInt64(), window.ProcessId));
            }
        }

        // Список записывается до сворачивания: после сбоя сторож развернёт и эти окна.
        var previous = _mark;
        _mark = Context.Changes.Record(MinimizedWindowsRestorer.ChangeKind, MinimizedWindowsRestorer.ToData(_minimized));
        previous?.Dispose();
        foreach (var window in extra)
        {
            WindowControl.RequestMinimize(window.Handle);
        }
    }

    private bool Counts(WindowInfo window) =>
        window.ProcessId != Environment.ProcessId && !WindowControl.IsDesktop(window) && Scope(window);
}
