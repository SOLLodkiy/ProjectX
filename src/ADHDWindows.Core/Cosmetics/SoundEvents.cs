namespace ADHDWindows.Core.Cosmetics;

/// <summary>
/// Настройки звука программы. <paramref name="Volume"/> и <paramref name="MusicVolume"/> — от 0 до 100,
/// поверх системной громкости.
/// </summary>
public sealed record SoundSettings(bool Effects, bool Music, int Volume, int MusicVolume)
{
    public const int MaxVolume = 100;

    public static SoundSettings Default { get; } = new(true, false, 60, 40);

    public SoundSettings Normalized() => this with
    {
        Volume = Math.Clamp(Volume, 0, MaxVolume),
        MusicVolume = Math.Clamp(MusicVolume, 0, MaxVolume),
    };
}

public sealed record SoundSettingsRequested;

/// <summary>Игрок изменил настройки звука.</summary>
public sealed record SoundSettingsChangeRequested(SoundSettings Settings);

public sealed record SoundSettingsChanged(SoundSettings Settings);

/// <summary>
/// Проиграть звук по названию из data/sound/cues.json. Так любой модуль озвучивает свои события без правки модуля звука.
/// </summary>
public sealed record SoundCueRequested(string Cue);

/// <summary>Звук прозвучал. Для проверок и статистики.</summary>
public sealed record SoundCuePlayed(string Cue);
