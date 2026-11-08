using ADHDWindows.Core.Data;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Modules.Hardships.Basic;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Sounds;

/// <summary>Настройки «Случайных звуков» из data/hardships/sounds.json.</summary>
public sealed record SoundsData(double MinSeconds, double MaxSeconds, IReadOnlyList<string> System, string Folder)
{
    public const string DataKey = "hardships/sounds";

    public static SoundsData Default { get; } = new(20, 60, ["Windows Critical Stop.wav", "Windows Shutdown.wav", "Windows Hardware Insert.wav"], "sounds/random");

    public static SoundsData Load(GameData data) =>
        data.TryGet<SoundsData>(DataKey, out var value) && value is { System: not null, Folder: not null } && value.MaxSeconds >= value.MinSeconds && value.MinSeconds > 0
            ? value
            : Default;

    /// <summary>
    /// Файлы, которые можно играть: звуки Windows с этого компьютера и все .wav из своей папки в assets.
    /// Отсутствующие файлы пропускаются.
    /// </summary>
    public IReadOnlyList<string> Files(string systemFolder, string? ownFolder)
    {
        var files = System
            .Select(name => Path.Combine(systemFolder, name))
            .Where(File.Exists)
            .ToList();
        if (ownFolder is not null && Directory.Exists(ownFolder))
        {
            files.AddRange(Directory.GetFiles(ownFolder, "*.wav").Order(StringComparer.OrdinalIgnoreCase));
        }

        return files;
    }
}

/// <summary>
/// Случайные звуки: компьютер иногда проигрывает странные звуки — ошибку, выключение, подключение USB, стук в дверь,
/// шаги, капающую воду. Звук не громче системного и не играет при «Не беспокоить» и во время созвона.
/// </summary>
public sealed class RandomSoundsHardship : HardshipModule
{
    /// <summary>Первый звук звучит раньше обычной паузы: так понятно, что усложнение началось.</summary>
    public const double FirstPauseShare = 0.3;

    private readonly Lock _lock = new();
    private readonly List<string> _played = [];
    private QuietWatch? _quiet;
    private Ticker? _ticker;
    private IReadOnlyList<string> _files = [];
    private Random _random = new();
    private double _minPause;
    private double _maxPause;
    private double _wait;
    private string? _last;
    private int _skipped;

    public override HardshipInfo Info { get; } = new("randomSounds", HardshipLevel.Easy, HardshipResources.Sound);

    /// <summary>Какие файлы прозвучали, для проверок.</summary>
    internal IReadOnlyList<string> Played
    {
        get
        {
            lock (_lock)
            {
                return [.. _played];
            }
        }
    }

    /// <summary>Сколько звуков пропущено из-за «Не беспокоить».</summary>
    internal int Skipped
    {
        get
        {
            lock (_lock)
            {
                return _skipped;
            }
        }
    }

    /// <summary>Файлы, из которых выбирается звук.</summary>
    internal IReadOnlyList<string> Files => _files;

    /// <summary>Подмена проигрывания для проверок.</summary>
    internal Func<string, bool> Play { get; set; } = Platform.Sounds.Play;

    protected override void OnStart() => _quiet = new QuietWatch(Context.Events);

    protected override void Begin()
    {
        var data = SoundsData.Load(Context.Data);
        _files = data.Files(Platform.Sounds.SystemFolder, AssetFiles.Find(data.Folder));
        if (_files.Count == 0)
        {
            throw new InvalidOperationException("Нет ни одного звука");
        }

        _random = new Random();
        var spread = new Spread(_random);
        lock (_lock)
        {
            (_minPause, _maxPause) = spread.Vary(data.MinSeconds, data.MaxSeconds);
            _wait = Pause() * FirstPauseShare;
            _played.Clear();
            _last = null;
            _skipped = 0;
        }

        _ticker = new Ticker("RandomSounds", Step, error => Context.Log.Error("Ошибка случайных звуков", error), intervalMs: 100);
    }

    protected override void End()
    {
        _ticker?.Dispose();
        _ticker = null;
        Platform.Sounds.Stop();
        lock (_lock)
        {
            Context.Log.Info($"Случайные звуки: прозвучало {_played.Count}, пропущено {_skipped}");
        }
    }

    /// <summary>Проиграть следующий звук сейчас, для проверок.</summary>
    internal void PlayNowForTest()
    {
        lock (_lock)
        {
            _wait = 0;
        }

        Step(0);
    }

    private void Step(double seconds)
    {
        string file;
        lock (_lock)
        {
            _wait -= seconds;
            if (_wait > 0)
            {
                return;
            }

            _wait = Pause();
            if (_quiet?.IsQuiet == true)
            {
                _skipped++;
                return;
            }

            // Один и тот же звук не звучит два раза подряд.
            var choices = _files.Count > 1 ? _files.Where(path => path != _last).ToList() : [.. _files];
            file = choices[_random.Next(choices.Count)];
            _last = file;
        }

        if (Play(file))
        {
            lock (_lock)
            {
                _played.Add(file);
            }
        }
        else
        {
            Context.Log.Warn($"Звук не проигран: {file}");
        }
    }

    private double Pause() => _minPause + (_random.NextDouble() * (_maxPause - _minPause));
}
