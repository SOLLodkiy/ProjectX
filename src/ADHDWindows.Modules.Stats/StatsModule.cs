using System.Diagnostics;
using ADHDWindows.Core.Display;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Media;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Stats;
using ADHDWindows.Core.Upgrades;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Stats;

/// <summary>
/// Логи и статистика: собирает события игры, пройденный курсором путь и время в программах.
/// Статистика лежит только в сохранении на этом компьютере и никуда не отправляется.
/// Окну отвечает событием <see cref="StatsChanged"/> на <see cref="StatsRequested"/>.
/// </summary>
public sealed class StatsModule : IModule
{
    /// <summary>Как часто читается положение курсора. Путь между замерами считается по прямой.</summary>
    private static readonly TimeSpan CursorInterval = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan ProgramInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Без ввода дольше этого время программы в активном окне не считается: человек отошёл.</summary>
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(2);

    /// <summary>Скачок курсора дальше этого — смена экрана или перенос программой, а не движение руки.</summary>
    private const double MaxCursorJump = 4000;

    private readonly CancellationTokenSource _stop = new();
    private readonly InputActivity _input = new();
    private IModuleContext? _context;
    private StatsRecorder? _recorder;
    private Thread? _worker;
    private (ProcessStamp Stamp, string? Name)? _programCache;

    public string Id => "stats";

    public string Name => "Логи и статистика";

    public void Start(IModuleContext context)
    {
        _context = context;
        var recorder = _recorder = new StatsRecorder(context.Saves, TimeProvider.System);
        var texts = context.Texts;

        context.Events.Subscribe<CoinsEarned>(recorder.OnEarned);
        context.Events.Subscribe<CoinsSpent>(recorder.OnSpent);
        context.Events.Subscribe<ClickCounted>(recorder.OnClick);
        context.Events.Subscribe<MediaTimeCounted>(recorder.OnMedia);
        context.Events.Subscribe<StatCounted>(recorder.OnCounter);
        context.Events.Subscribe<CollectionChanged>(recorder.OnCollection);
        context.Events.Subscribe<FeedPosted>(posted => recorder.Post(posted.Icon, posted.Text));
        context.Events.Subscribe<ItemUnlocked>(unlocked => recorder.Post(
            "unlock",
            texts.Get("feed.unlocked", unlocked.Offer.Name, CoinFormat.Short(unlocked.Offer.Price))));
        context.Events.Subscribe<UpgradePurchased>(purchased => recorder.OnUpgrade(
            purchased,
            texts.Get("feed.upgrade", purchased.Name, purchased.Level, CoinFormat.Short(purchased.Price))));
        context.Events.Subscribe<StatsRequested>(_ => context.Events.Publish(new StatsChanged(recorder.Snapshot(context.Economy.Balance))));

        _worker = new Thread(Work) { IsBackground = true, Name = "Stats", Priority = ThreadPriority.BelowNormal };
        _worker.Start();

        // Модуль блокировки запускается раньше и мог уже разослать коллекцию.
        context.Events.Publish(new CollectionRequested());
    }

    public void Stop()
    {
        _stop.Cancel();
        if (_worker?.Join(StopTimeout) == false)
        {
            _context?.Log.Warn("Поток статистики не закрылся вовремя");
        }

        _recorder?.Save();
    }

    private void Work()
    {
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed;
        var nextProgram = last + ProgramInterval;
        var nextSave = last + SaveInterval;
        ScreenPoint? cursor = null;
        while (!_stop.Token.WaitHandle.WaitOne(CursorInterval))
        {
            try
            {
                var now = clock.Elapsed;
                _recorder!.AddAppTime((now - last).TotalSeconds);
                last = now;

                var point = Desktop.CursorPosition();
                if (cursor is { } previous)
                {
                    var distance = Math.Sqrt(Math.Pow(point.X - previous.X, 2) + Math.Pow(point.Y - previous.Y, 2));
                    if (distance <= MaxCursorJump)
                    {
                        _recorder.AddCursor(distance);
                    }
                }

                cursor = point;

                if (now >= nextProgram)
                {
                    if (_input.Idle() < IdleLimit && ForegroundProgram() is { } program)
                    {
                        _recorder.AddProgram(program, ProgramInterval.TotalSeconds);
                    }

                    nextProgram = now + ProgramInterval;
                }

                if (now >= nextSave)
                {
                    _recorder.Save();
                    nextSave = now + SaveInterval;
                }
            }
            catch (Exception exception)
            {
                _context?.Log.Error("Ошибка статистики", exception);
            }
        }
    }

    /// <summary>Название программы в активном окне: описание файла, а без него — имя файла.</summary>
    private string? ForegroundProgram()
    {
        if (WindowControl.ForegroundProcessId() is not { } id || ProcessControl.Stamp(id) is not { } stamp)
        {
            return null;
        }

        if (_programCache is not { } cached || cached.Stamp != stamp)
        {
            _programCache = cached = (stamp, Describe(ProcessControl.ImagePath(stamp)));
        }

        return cached.Name;
    }

    private static string? Describe(string? path)
    {
        if (path is null)
        {
            return null;
        }

        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description))
            {
                return description;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Имя файла подойдёт.
        }

        return Path.GetFileNameWithoutExtension(path);
    }
}
