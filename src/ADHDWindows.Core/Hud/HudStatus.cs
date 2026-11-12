namespace ADHDWindows.Core.Hud;

/// <summary>Строка мини-панели, которую заполняют механики.</summary>
public enum HudSlot
{
    Hardship,
    Quest,

    /// <summary>Действующие бустеры случайных событий.</summary>
    Event,
}

/// <summary>Новый текст строки мини-панели. Значение null означает, что показывать нечего.</summary>
public sealed record HudStatusChanged(HudSlot Slot, string? Text);
