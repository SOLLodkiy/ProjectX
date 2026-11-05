using ADHDWindows.Core.Display;
using ADHDWindows.Core.Pixels;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Core.Hud;

/// <summary>Настройки HUD. Каждое изменение сразу попадает в хранилище.</summary>
public sealed class HudSettings(IKeyValueStore store)
{
    public const int MinScale = 1;
    public const int MaxScale = 4;
    public const int MinOpacity = 25;
    public const int MaxOpacity = 100;

    private const string PositionPrefix = "hud/position/";
    private const string LastPositionKey = "hud/lastPosition";

    /// <summary>Срабатывает после изменения любой настройки, кроме положения.</summary>
    public event Action? Changed;

    public bool Visible
    {
        get => store.Get("hud/visible", true);
        set => Write("hud/visible", value);
    }

    /// <summary>Целочисленный размер: сколько пикселей экрана занимает пиксель арта при масштабе монитора 100%.</summary>
    public int Scale
    {
        get => Math.Clamp(store.Get("hud/scale", PixelScale.DefaultUiScale), MinScale, MaxScale);
        set => Write("hud/scale", Math.Clamp(value, MinScale, MaxScale));
    }

    /// <summary>Непрозрачность в процентах.</summary>
    public int Opacity
    {
        get => Math.Clamp(store.Get("hud/opacity", MaxOpacity), MinOpacity, MaxOpacity);
        set => Write("hud/opacity", Math.Clamp(value, MinOpacity, MaxOpacity));
    }

    public bool ShowFlyouts
    {
        get => store.Get("hud/flyouts", true);
        set => Write("hud/flyouts", value);
    }

    public bool HideInFullscreen
    {
        get => store.Get("hud/hideInFullscreen", false);
        set => Write("hud/hideInFullscreen", value);
    }

    /// <summary>Положение для конфигурации мониторов, а если её ещё не было, последнее известное.</summary>
    public ScreenPoint? Position(string configuration) =>
        store.Get<ScreenPoint?>(PositionPrefix + configuration) ?? store.Get<ScreenPoint?>(LastPositionKey);

    public void RememberPosition(string configuration, ScreenPoint position)
    {
        store.Set(PositionPrefix + configuration, position);
        store.Set(LastPositionKey, position);
    }

    private void Write<T>(string key, T value)
    {
        store.Set(key, value);
        Changed?.Invoke();
    }
}
