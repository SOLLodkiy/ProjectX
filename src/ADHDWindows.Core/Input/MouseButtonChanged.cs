namespace ADHDWindows.Core.Input;

public enum MouseButton
{
    Left,
    Right,
    Middle,
}

/// <summary>
/// Нажатие или отпускание кнопки мыши в любом месте системы. Приходит не в потоке интерфейса.
/// <paramref name="Injected"/> равен true, если событие создано программой, а не устройством.
/// <paramref name="Time"/> — монотонное время события: его можно сравнивать только с другими такими же значениями.
/// </summary>
public sealed record MouseButtonChanged(MouseButton Button, bool Pressed, bool Injected, TimeSpan Time);
