using ADHDWindows.Core.Display;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Pixels;
using ADHDWindows.Modules.Hardships.Screen;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Keyboard;

/// <summary>
/// Виртуальная клавиатура: обычная клавиатура перестаёт печатать — нажатия клавиш с текстом исчезают, — а печатать
/// можно мышью на пиксельной клавиатуре внизу монитора. Её можно перетаскивать за заголовок. Клик по ней не забирает
/// фокус у программы, поэтому символы идут туда, где человек печатал. Защищённые места, сочетания с Ctrl, Alt и Win,
/// стрелки, функциональные клавиши и аварийная клавиша не трогаются.
/// </summary>
public sealed class VirtualKeyboardHardship : OverlayHardship
{
    public const double PressSeconds = 0.12;
    public const int BottomMargin = 16;

    private readonly Lock _lock = new();
    private readonly HashSet<int> _swallowed = [];
    private VirtualKeyboard? _keyboard;
    private SpriteOverlay? _sprite;
    private KeyboardFilter? _filter;
    private KeyboardGuard? _guard;
    private PixelFont? _font;
    private string _title = string.Empty;
    private int _scale = 3;
    private VirtualKey? _pressed;
    private double _pressedFor;
    private bool _dirty;
    private (int X, int Y)? _drag;
    private ScreenPoint _position;
    private int _blocked;
    private int _typed;

    public override HardshipInfo Info { get; } = new("virtualKeyboard", HardshipLevel.Hard, HardshipResources.Keyboard | HardshipResources.Screen);

    /// <summary>Клавиатура, для проверок.</summary>
    internal VirtualKeyboard? Keyboard
    {
        get
        {
            lock (_lock)
            {
                return _keyboard;
            }
        }
    }

    /// <summary>Защита, для проверок.</summary>
    internal KeyboardGuard? Guard => _guard;

    /// <summary>Где клавиатура на экране, для проверок.</summary>
    internal ScreenRect? Bounds => Overlay.Invoke(() => _sprite is { IsVisible: true } sprite ? sprite.Bounds : (ScreenRect?)null);

    /// <summary>Подмена ввода для проверок: что набрано на виртуальной клавиатуре.</summary>
    internal Action<VirtualPress>? Output { get; set; }

    internal int Blocked
    {
        get
        {
            lock (_lock)
            {
                return _blocked;
            }
        }
    }

    protected override void Begin()
    {
        var data = KeyboardData.Load(Context.Data);
        lock (_lock)
        {
            _keyboard = new VirtualKeyboard([.. data.Layouts.Values]);
            _swallowed.Clear();
            _pressed = null;
            _drag = null;
            _blocked = 0;
            _typed = 0;
        }

        _font = OverlayFont.Body(Context.Log);
        _title = Context.Texts.Get("hardships.virtualKeyboard.title");
        _guard = new KeyboardGuard(data.Protected, Context.Log);
        base.Begin();
        _filter = new KeyboardFilter(Filter, Context.Log);
        if (!_filter.Start())
        {
            throw new InvalidOperationException("Фильтр клавиатуры не поставлен");
        }
    }

    protected override void End()
    {
        // Сначала хук: с этого момента обычная клавиатура снова печатает.
        _filter?.Dispose();
        _filter = null;
        base.End();
        _guard?.Dispose();
        _guard = null;
    }

    protected override void Create()
    {
        var area = OverlayArea.WorkArea();
        _scale = area.Width >= 1600 ? 3 : 2;
        _sprite = Overlay.CreatePointerSprite(OnPointer);
        var keyboard = _keyboard!;
        _position = new ScreenPoint(
            area.Left + ((area.Width - (keyboard.Width * _scale)) / 2),
            area.Bottom - (keyboard.Height * _scale) - BottomMargin);
        Draw();
    }

    protected override void Tick(double seconds)
    {
        if (_pressedFor > 0)
        {
            _pressedFor -= seconds;
            if (_pressedFor <= 0)
            {
                lock (_lock)
                {
                    _pressed = null;
                }

                _dirty = true;
            }
        }

        if (_dirty)
        {
            Draw();
        }
    }

    protected override void Finish()
    {
        lock (_lock)
        {
            Context.Log.Info($"Виртуальная клавиатура: набрано {_typed}, исчезло нажатий {_blocked}");
            _keyboard = null;
        }

        _sprite = null;
    }

    /// <summary>Нажать клавишу мышью, для проверок: как клик по ней.</summary>
    internal void ClickForTest(VirtualKey key) => Overlay.Invoke(() =>
    {
        OnPointer(SpritePointer.Down, key.Bounds.Left + 1, key.Bounds.Top + 1);
        OnPointer(SpritePointer.Up, key.Bounds.Left + 1, key.Bounds.Top + 1);
    });

    /// <summary>Перетащить за заголовок на (<paramref name="dx"/>, <paramref name="dy"/>) пикселей, для проверок.</summary>
    internal void DragForTest(int dx, int dy) => Overlay.Invoke(() =>
    {
        OnPointer(SpritePointer.Down, 2, 2);
        OnPointer(SpritePointer.Move, 2 + (dx / _scale), 2 + (dy / _scale));
        OnPointer(SpritePointer.Up, 2 + (dx / _scale), 2 + (dy / _scale));
    });

    /// <summary>Решение фильтра без хука, для проверок: true — нажатие исчезло.</summary>
    internal bool Filter(KeyStroke stroke) => Decide(stroke, KeyboardFilter.ShortcutModifierDown());

    internal bool Decide(KeyStroke stroke, bool shortcut)
    {
        lock (_lock)
        {
            if (!stroke.Down)
            {
                return _swallowed.Remove(stroke.VirtualKey);
            }

            if (shortcut || _guard?.IsProtected != false || !Types(stroke.VirtualKey))
            {
                return false;
            }

            _swallowed.Add(stroke.VirtualKey);
            _blocked++;
            return true;
        }
    }

    /// <summary>Печатает ли клавиша текст: буквы, цифры, знаки, пробел, Enter и Backspace. Остальные клавиши работают.</summary>
    public static bool Types(int virtualKey) => virtualKey switch
    {
        >= 0x30 and <= 0x39 => true,
        >= 0x41 and <= 0x5A => true,
        >= 0x60 and <= 0x6F => true,
        >= 0xBA and <= 0xC0 => true,
        >= 0xDB and <= 0xDF => true,
        0xE2 or 0x20 or 0x0D or 0x08 => true,
        _ => false,
    };

    private void OnPointer(SpritePointer action, int x, int y)
    {
        VirtualPress? press = null;
        lock (_lock)
        {
            if (_keyboard is null)
            {
                return;
            }

            switch (action)
            {
                case SpritePointer.Down when _keyboard.InTitle(x, y):
                    _drag = (x, y);
                    break;
                case SpritePointer.Down when _keyboard.KeyAt(x, y) is { } key:
                    press = _keyboard.Press(key);
                    _pressed = key;
                    _pressedFor = PressSeconds;
                    _dirty = true;
                    break;
                case SpritePointer.Move when _drag is { } grip:
                    _position = new ScreenPoint(_position.X + ((x - grip.X) * _scale), _position.Y + ((y - grip.Y) * _scale));
                    _sprite?.MoveTo(_position);
                    break;
                case SpritePointer.Up:
                    _drag = null;
                    break;
            }

            if (press is not null)
            {
                _typed++;
            }
        }

        if (press is not null)
        {
            Send(press);
        }
    }

    private void Send(VirtualPress press)
    {
        if (Output is { } output)
        {
            output(press);
            return;
        }

        try
        {
            if (press.Character is { } character)
            {
                KeyboardInput.Type(character);
            }
            else if (press.Press is { } key)
            {
                KeyboardInput.Press(key);
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Context.Log.Warn($"Символ с виртуальной клавиатуры не напечатан: {exception.Message}");
        }
    }

    private void Draw()
    {
        _dirty = false;
        Picture picture;
        lock (_lock)
        {
            if (_keyboard is null || _sprite is null)
            {
                return;
            }

            picture = VirtualKeyboardArt.Render(_keyboard, _font, _title, _pressed);
        }

        _sprite.Show(picture.Pixels, picture.Width, picture.Height, _scale, _position);
    }
}
