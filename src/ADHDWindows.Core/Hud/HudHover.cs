namespace ADHDWindows.Core.Hud;

/// <summary>
/// Когда открывать мини-панель. Открывается после короткой задержки на ручке, а закрывается с запасом,
/// чтобы курсор успел перейти с ручки на панель.
/// </summary>
public sealed class HudHover
{
    public static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(400);

    private DateTimeOffset? _since;

    public bool IsOpen { get; private set; }

    /// <summary>Учитывает новое положение курсора и возвращает, должна ли панель быть открыта.</summary>
    public bool Update(bool overHandle, bool overPanel, DateTimeOffset now)
    {
        var wanted = overHandle || (IsOpen && overPanel);
        if (wanted == IsOpen)
        {
            _since = null;
            return IsOpen;
        }

        _since ??= now;
        if (now - _since >= (IsOpen ? CloseDelay : OpenDelay))
        {
            IsOpen = wanted;
            _since = null;
        }

        return IsOpen;
    }

    /// <summary>Сразу закрывает панель, например в начале перетаскивания.</summary>
    public void Reset()
    {
        IsOpen = false;
        _since = null;
    }
}
