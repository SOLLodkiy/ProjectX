using ADHDWindows.Core.Cosmetics;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Progress;
using ADHDWindows.Core.Quests;
using ADHDWindows.Core.RandomEvents;
using ADHDWindows.Core.Upgrades;
using ADHDWindows.Modules.Hardships.Basic;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Sound;

/// <summary>Звук из data/sound/cues.json: файл в assets/sounds и громкость относительно общей, от 0 до 1.</summary>
public sealed record SoundCue(string File, double Volume = 1);

/// <summary>Звуки и музыка программы. <see cref="Music"/> — файлы фоновой мелодии, играют по кругу.</summary>
public sealed record SoundData(IReadOnlyDictionary<string, SoundCue> Cues, IReadOnlyList<string> Music)
{
    public const string DataKey = "sound/cues";

    public static SoundData Load(GameData data, ILog log)
    {
        if (data.TryGet<SoundData>(DataKey, out var value) && value is { Cues: not null, Music: not null })
        {
            return value;
        }

        log.Warn($"Нет звуков {DataKey}: программа молчит");
        return new SoundData(new Dictionary<string, SoundCue>(), []);
    }
}

/// <summary>Куда идёт звук. В программе — микшер waveOut, в проверках — запись.</summary>
public interface ISoundOutput : IDisposable
{
    void Play(short[] samples, float volume);

    void SetMusic(short[]? samples, float volume);

    void Silence();
}

internal sealed class MixerOutput : ISoundOutput
{
    private readonly SoundMixer _mixer = new();

    public void Play(short[] samples, float volume) => _mixer.Play(samples, volume);

    public void SetMusic(short[]? samples, float volume) => _mixer.SetMusic(samples, volume);

    public void Silence() => _mixer.Silence();

    public void Dispose() => _mixer.Dispose();
}

/// <summary>
/// 8-битные звуки событий игры и необязательная фоновая мелодия, созданные инструментом ресурсов. Звуки привязаны
/// к событиям шины здесь, а сами файлы и громкость — в данных; любой модуль может попросить звук через
/// <see cref="SoundCueRequested"/>. При «Не беспокоить», созвоне, в полноэкранном окне и на защищённом экране звука нет.
/// Музыка замолкает, пока человек отошёл. Звук и музыка выключаются в настройках.
/// </summary>
public sealed class SoundModule : IModule
{
    public const string SettingsKey = "settings";

    /// <summary>Музыка замолкает, если человек ничего не делает столько минут.</summary>
    public const double AwayMinutes = 5;

    private static readonly TimeSpan MinRepeat = TimeSpan.FromMilliseconds(120);

    private readonly Func<ISoundOutput> _createOutput;
    private readonly Func<TimeSpan> _idle;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, short[]> _samples = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastPlayed = new(StringComparer.Ordinal);
    private IModuleContext? _context;
    private ISoundOutput? _output;
    private SoundData _data = new(new Dictionary<string, SoundCue>(), []);
    private SoundSettings _settings = SoundSettings.Default;
    private short[]? _music;
    private Timer? _awayTimer;
    private volatile bool _quiet;
    private bool _away;

    public SoundModule()
        : this(() => new MixerOutput(), new InputActivity().Idle, TimeProvider.System)
    {
    }

    internal SoundModule(Func<ISoundOutput> output, Func<TimeSpan> idle, TimeProvider clock)
    {
        _createOutput = output;
        _idle = idle;
        _clock = clock;
    }

    public string Id => "sound";

    public string Name => "Звук";

    internal SoundSettings Settings
    {
        get
        {
            lock (_lock)
            {
                return _settings;
            }
        }
    }

    /// <summary>Музыка сейчас играет, для проверок.</summary>
    internal bool MusicPlaying { get; private set; }

    public void Start(IModuleContext context)
    {
        _context = context;
        _data = SoundData.Load(context.Data, context.Log);
        _settings = (context.Settings.Get<SoundSettings>(SettingsKey) ?? SoundSettings.Default).Normalized();
        LoadFiles(context.Log);
        _output = _createOutput();

        var events = context.Events;
        events.Subscribe<SoundSettingsRequested>(_ => events.Publish(new SoundSettingsChanged(Settings)));
        events.Subscribe<SoundSettingsChangeRequested>(request => Change(request.Settings));
        events.Subscribe<SoundCueRequested>(request => Play(request.Cue));
        events.Subscribe<HardshipStatus>(status =>
        {
            _quiet = status.Blocked is not null || (status.QuietUntil is { } until && until > _clock.GetUtcNow());
            UpdateMusic();
        });

        // Звуки событий игры. Новое событие озвучивается строкой здесь или просьбой SoundCueRequested из своего модуля.
        events.Subscribe<UpgradePurchased>(_ => Play("purchase"));
        events.Subscribe<ItemUnlocked>(_ => Play("purchase"));
        events.Subscribe<CosmeticPurchased>(_ => Play("purchase"));
        events.Subscribe<UpgradePurchaseDenied>(_ => Play("denied"));
        events.Subscribe<UnlockDenied>(_ => Play("denied"));
        events.Subscribe<CosmeticPurchaseDenied>(_ => Play("denied"));
        events.Subscribe<PetFeedDenied>(_ => Play("denied"));
        events.Subscribe<QuestCompleted>(_ => Play("quest"));
        events.Subscribe<LevelReached>(_ => Play("level"));
        events.Subscribe<AchievementUnlocked>(_ => Play("achievement"));
        events.Subscribe<ChestOpened>(_ => Play("chest"));
        events.Subscribe<RandomEventStarted>(_ => Play("event"));
        events.Subscribe<HardshipWarned>(_ => Play("warning"));
        events.Subscribe<HardshipEnded>(ended =>
        {
            if (ended.Reason == HardshipEndReason.Survived)
            {
                Play("survived");
            }
        });
        events.Subscribe<CosmeticGranted>(_ => Play("chest"));
        events.Subscribe<PetFed>(_ => Play("feed"));
        events.Subscribe<PetReacted>(reaction =>
        {
            if (reaction.Reason == "petted")
            {
                Play("pet");
            }
        });

        _awayTimer = new Timer(_ => CheckAway(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        context.Log.Info($"Звуков: {_samples.Count}, музыка: {(_music is null ? "нет" : "есть")}, звук {(_settings.Effects ? "включён" : "выключен")}");
        events.Publish(new HardshipStatusRequested());
        events.Publish(new SoundSettingsChanged(Settings));
        UpdateMusic();
    }

    public void Stop()
    {
        _awayTimer?.Dispose();
        _awayTimer = null;
        _output?.Silence();
        _output?.Dispose();
        _output = null;
    }

    /// <summary>Проигрывает звук. False — звук выключен, тишина или такого звука нет.</summary>
    internal bool Play(string cue)
    {
        short[]? samples;
        float volume;
        lock (_lock)
        {
            if (!_settings.Effects || _settings.Volume == 0 || _quiet || _output is null
                || !_samples.TryGetValue(cue, out samples) || !_data.Cues.TryGetValue(cue, out var data))
            {
                return false;
            }

            // Одно событие часто приходит несколько раз подряд: звук не повторяется чаще, чем раз в доли секунды.
            var now = _clock.GetUtcNow();
            if (_lastPlayed.TryGetValue(cue, out var last) && now - last < MinRepeat)
            {
                return false;
            }

            _lastPlayed[cue] = now;
            volume = Loudness(_settings.Volume) * (float)Math.Clamp(data.Volume, 0, 1);
        }

        _output.Play(samples, volume);
        _context?.Events.Publish(new SoundCuePlayed(cue));
        return true;
    }

    internal void Change(SoundSettings settings)
    {
        settings = settings.Normalized();
        lock (_lock)
        {
            _settings = settings;
            _context?.Settings.Set(SettingsKey, settings);
        }

        if (!settings.Effects)
        {
            _output?.Silence();
        }

        _context?.Log.Info($"Звук: эффекты {settings.Effects} {settings.Volume}%, музыка {settings.Music} {settings.MusicVolume}%");
        UpdateMusic();
        _context?.Events.Publish(new SoundSettingsChanged(settings));
    }

    /// <summary>Громкость на слух растёт быстрее, чем линейно: середина шкалы — примерно четверть мощности.</summary>
    internal static float Loudness(int percent) => (float)Math.Pow(Math.Clamp(percent, 0, 100) / 100.0, 2);

    internal void CheckAwayForTest() => CheckAway();

    private void CheckAway()
    {
        var away = _idle() >= TimeSpan.FromMinutes(AwayMinutes);
        lock (_lock)
        {
            if (away == _away)
            {
                return;
            }

            _away = away;
        }

        UpdateMusic();
    }

    private void UpdateMusic()
    {
        bool play;
        float volume;
        lock (_lock)
        {
            play = _settings.Music && _settings.MusicVolume > 0 && _music is not null && !_quiet && !_away;
            volume = Loudness(_settings.MusicVolume);
        }

        _output?.SetMusic(play ? _music : null, volume);
        MusicPlaying = play;
    }

    private void LoadFiles(ILog log)
    {
        var folder = AssetFiles.Find("sounds");
        if (folder is null)
        {
            log.Warn("Папка звуков не найдена: программа молчит");
            return;
        }

        foreach (var (cue, data) in _data.Cues)
        {
            if (Read(Path.Combine(folder, data.File), log) is { } samples)
            {
                _samples[cue] = samples;
            }
        }

        var music = _data.Music.Select(file => Read(Path.Combine(folder, file), log)).OfType<short[]>().ToList();
        if (music.Count > 0)
        {
            _music = music.SelectMany(part => part).ToArray();
        }
    }

    private static short[]? Read(string path, ILog log)
    {
        try
        {
            return WaveFile.Read(path);
        }
        catch (Exception exception) when (exception is IOException or FormatException or UnauthorizedAccessException)
        {
            log.Warn($"Звук не прочитан: {path}: {exception.Message}");
            return null;
        }
    }
}
