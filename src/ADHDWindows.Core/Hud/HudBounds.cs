using ADHDWindows.Core.Display;

namespace ADHDWindows.Core.Hud;

/// <summary>
/// HUD переместился, показался или скрылся. <paramref name="Bar"/> — полоска HUD в пикселях экрана.
/// По нему рядом с HUD садится питомец.
/// </summary>
public sealed record HudMoved(ScreenRect Bar, bool Visible);

/// <summary>Модулю нужно положение HUD. HUD отвечает <see cref="HudMoved"/>; без HUD ответа нет.</summary>
public sealed record HudBoundsRequested;
