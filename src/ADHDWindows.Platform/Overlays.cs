using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ADHDWindows.Core.Display;

namespace ADHDWindows.Platform;

/// <summary>
/// Поток окон поверх экрана: свой цикл сообщений и действие примерно 60 раз в секунду. Окна оверлеев создаются
/// и меняются только в этом потоке — из <see cref="OverlayThread"/> tick или через <see cref="Invoke"/>.
/// Оверлеи не получают фокус, не видны в Alt+Tab и держатся ниже окон самой программы ADHDWindows,
/// чтобы экран покупки и HUD оставались видны. Окна принадлежат процессу и исчезают вместе с ним,
/// поэтому для отката после сбоя ничего записывать не нужно.
/// </summary>
public sealed class OverlayThread : IDisposable
{
    internal const string ClassPrefix = "ADHDWindows.Overlay";

    private const uint QuitMessage = 0x0012;
    private const uint AllInput = 0x04FF;
    private const uint InputAvailable = 0x0004;
    private const uint Remove = 0x0001;
    private static readonly TimeSpan OrderInterval = TimeSpan.FromSeconds(1);

    private readonly ConcurrentQueue<(Action Action, ManualResetEventSlim? Done)> _actions = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly List<OverlayWindow> _windows = [];
    private readonly Action<double> _tick;
    private readonly Action<Exception> _error;
    private readonly int _intervalMs;
    private readonly Thread _thread;
    private volatile bool _stopping;

    public OverlayThread(string name, Action<double> tick, Action<Exception> error, int intervalMs = 16)
    {
        _tick = tick;
        _error = error;
        _intervalMs = intervalMs;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready))
        {
            IsBackground = true,
            Name = name,
        };
        _thread.Start();
        ready.Wait();
    }

    /// <summary>Сколько окон оверлея сейчас живо, для проверок.</summary>
    public int WindowCount => Invoke(() => _windows.Count);

    /// <summary>Выполняет действие в потоке оверлея и ждёт его.</summary>
    public void Invoke(Action action) => Invoke(() =>
    {
        action();
        return 0;
    });

    public T Invoke<T>(Func<T> action)
    {
        if (Thread.CurrentThread == _thread)
        {
            return action();
        }

        T result = default!;
        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        _actions.Enqueue((
            () =>
            {
                try
                {
                    result = action();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            },
            done));
        _wake.Set();
        if (!_thread.IsAlive || !done.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("Поток оверлея не отвечает");
        }

        return failure is null ? result : throw new InvalidOperationException("Ошибка в потоке оверлея", failure);
    }

    /// <summary>Сплошное окно одного цвета с формой из прямоугольников, мышь проходит сквозь него.</summary>
    public RegionOverlay CreateRegion(uint rgb, byte alpha) => Invoke(() => Track(new RegionOverlay(this, rgb, alpha)));

    /// <summary>Картинка с прозрачностью. Клик по видимой точке вызывает <paramref name="click"/>, если он задан.</summary>
    public SpriteOverlay CreateSprite(Action<int, int>? click) => Invoke(() => Track(new SpriteOverlay(this, click)));

    /// <summary>
    /// Картинка, которую можно нажимать и тянуть: <paramref name="pointer"/> получает нажатие, движение с нажатой
    /// кнопкой и отпускание левой кнопки в точках картинки (до увеличения). Пока кнопка нажата, мышь захвачена окном.
    /// </summary>
    public SpriteOverlay CreatePointerSprite(Action<SpritePointer, int, int> pointer) =>
        Invoke(() => Track(new SpriteOverlay(this, null, pointer)));

    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _wake.Set();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        _wake.Dispose();
    }

    internal void Forget(OverlayWindow window) => _windows.Remove(window);

    private T Track<T>(T window)
        where T : OverlayWindow
    {
        _windows.Add(window);
        window.KeepOrder();
        return window;
    }

    private void Run(ManualResetEventSlim ready)
    {
        // Очередь сообщений потока нужна до первого окна.
        PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);
        ready.Set();
        var watch = Stopwatch.StartNew();
        var last = watch.Elapsed;
        var lastOrder = watch.Elapsed;
        var handles = new[] { _wake.SafeWaitHandle.DangerousGetHandle() };
        while (!_stopping)
        {
            var wait = Math.Max(0, _intervalMs - (int)(watch.Elapsed - last).TotalMilliseconds);
            MsgWaitForMultipleObjectsEx(1, handles, (uint)wait, AllInput, InputAvailable);
            while (PeekMessageW(out var message, IntPtr.Zero, 0, 0, Remove))
            {
                if (message.Message == QuitMessage)
                {
                    _stopping = true;
                    break;
                }

                DispatchMessageW(ref message);
            }

            while (_actions.TryDequeue(out var item))
            {
                item.Action();
                item.Done?.Set();
            }

            var now = watch.Elapsed;
            if ((now - last).TotalMilliseconds >= _intervalMs && !_stopping)
            {
                Guard(() => _tick(Math.Min((now - last).TotalSeconds, 0.1)));
                last = now;
            }

            if (now - lastOrder >= OrderInterval)
            {
                lastOrder = now;
                foreach (var window in _windows.ToList())
                {
                    Guard(window.KeepOrder);
                }
            }
        }

        foreach (var window in _windows.ToList())
        {
            Guard(window.Destroy);
        }

        while (_actions.TryDequeue(out var item))
        {
            item.Done?.Set();
        }
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            _error(exception);
        }
    }

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out NativeMessage message, IntPtr window, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref NativeMessage message);
}

/// <summary>Окно оверлея. Методы вызываются только в потоке своего <see cref="OverlayThread"/>.</summary>
public abstract class OverlayWindow
{
    private const int ExtendedStyleIndex = -20;
    private const uint Popup = 0x80000000;
    private const uint Layered = 0x00080000;
    private const uint Transparent = 0x00000020;
    private const uint TopMost = 0x00000008;
    private const uint ToolWindow = 0x00000080;
    private const uint NoActivate = 0x08000000;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoActivateFlag = 0x0010;
    private const uint ShowWindowFlag = 0x0040;
    private const uint HideWindowFlag = 0x0080;
    private const uint OwnerNoZOrder = 0x0200;

    private static readonly IntPtr TopMostWindow = new(-1);
    private static readonly string ClassName = OverlayThread.ClassPrefix + "." + Guid.NewGuid().ToString("N");
    private static readonly Dictionary<IntPtr, OverlayWindow> Live = [];
    private static readonly Lock ClassLock = new();
    private static WindowProcedure? _procedure;
    private static bool _registered;

    private readonly OverlayThread _owner;
    private bool _visible;

    private protected OverlayWindow(OverlayThread owner, bool clickThrough)
    {
        _owner = owner;
        EnsureClass();
        var style = Layered | TopMost | ToolWindow | NoActivate | (clickThrough ? Transparent : 0);
        Handle = CreateWindowExW(style, ClassName, string.Empty, Popup, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (Handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        lock (ClassLock)
        {
            Live[Handle] = this;
        }
    }

    public IntPtr Handle { get; private set; }

    public bool IsVisible => _visible;

    /// <summary>Закрывает окно. Повторный вызов безопасен.</summary>
    public void Destroy()
    {
        if (Handle == IntPtr.Zero)
        {
            return;
        }

        var handle = Handle;
        Handle = IntPtr.Zero;
        lock (ClassLock)
        {
            Live.Remove(handle);
        }

        DestroyWindow(handle);
        OnDestroyed();
        _owner.Forget(this);
    }

    /// <summary>Держит окно поверх других программ, но ниже окон ADHDWindows поверх всех (HUD, экран покупки).</summary>
    internal void KeepOrder()
    {
        if (Handle == IntPtr.Zero)
        {
            return;
        }

        var insertAfter = TopMostWindow;
        var process = Environment.ProcessId;
        EnumWindows(
            (window, _) =>
            {
                if (window != Handle
                    && IsWindowVisible(window)
                    && (GetWindowLongPtrW(window, ExtendedStyleIndex).ToInt64() & TopMost) != 0
                    && GetWindowThreadProcessId(window, out var owner) != 0
                    && owner == process
                    && !IsOverlay(window))
                {
                    insertAfter = window;
                }

                return true;
            },
            IntPtr.Zero);
        SetWindowPos(Handle, insertAfter, 0, 0, 0, 0, NoMove | NoSize | NoActivateFlag | OwnerNoZOrder);
    }

    private protected void SetVisible(bool visible)
    {
        if (visible == _visible || Handle == IntPtr.Zero)
        {
            return;
        }

        _visible = visible;
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, NoMove | NoSize | NoActivateFlag | 0x0004 | (visible ? ShowWindowFlag : HideWindowFlag));
    }

    private protected virtual void OnDestroyed()
    {
    }

    private protected virtual bool OnMessage(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        return false;
    }

    private static bool IsOverlay(IntPtr window)
    {
        var name = new StringBuilder(128);
        return GetClassNameW(window, name, name.Capacity) > 0 && name.ToString().StartsWith(OverlayThread.ClassPrefix, StringComparison.Ordinal);
    }

    private static void EnsureClass()
    {
        lock (ClassLock)
        {
            if (_registered)
            {
                return;
            }

            _procedure = Procedure;
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
                Instance = GetModuleHandleW(null),
                Cursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512)),
                ClassName = ClassName,
            };
            if (RegisterClassExW(ref windowClass) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _registered = true;
        }
    }

    private static IntPtr Procedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        OverlayWindow? overlay;
        lock (ClassLock)
        {
            Live.TryGetValue(window, out overlay);
        }

        // Клик по оверлею не активирует его и не забирает фокус у программы.
        const uint mouseActivate = 0x0021;
        if (message == mouseActivate)
        {
            return new IntPtr(3);
        }

        if (overlay is not null && overlay.OnMessage(message, wParam, lParam, out var result))
        {
            return result;
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WindowClass windowClass);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private protected static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumCallback callback, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int capacity);

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumCallback(IntPtr window, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public IntPtr Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }
}

/// <summary>
/// Окно во весь рабочий стол одного цвета и прозрачности. Видна только форма из прямоугольников — так дождь, туман
/// и темнота рисуются без перерисовки картинки размером с экран. Мышь всегда проходит сквозь окно.
/// </summary>
public sealed class RegionOverlay : OverlayWindow
{
    private const uint AlphaFlag = 0x00000002;
    private const int EraseBackground = 0x0014;
    private const int Paint = 0x000F;
    private const int RegionOr = 2;
    private const int RegionDiff = 4;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivateFlag = 0x0010;

    private readonly IntPtr _brush;
    private ScreenRect _area;

    internal RegionOverlay(OverlayThread owner, uint rgb, byte alpha)
        : base(owner, clickThrough: true)
    {
        // COLORREF хранит цвет как 0x00BBGGRR.
        _brush = CreateSolidBrush(((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF));
        SetLayeredWindowAttributes(Handle, 0, alpha, AlphaFlag);
    }

    /// <summary>
    /// Весь рабочий стол: все мониторы вместе. В автотестах — только тестовый монитор (<see cref="TestScreen"/>),
    /// чтобы оверлей не закрывал основной экран человека.
    /// </summary>
    public static ScreenRect VirtualScreen() =>
        TestScreen.Find()?.Bounds
        ?? new ScreenRect(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    /// <summary>
    /// Показывает форму: объединение <paramref name="include"/> без <paramref name="exclude"/>, в пикселях экрана.
    /// Пустая форма прячет окно.
    /// </summary>
    public void SetShape(IReadOnlyCollection<ScreenRect> include, IReadOnlyCollection<ScreenRect>? exclude = null)
    {
        if (Handle == IntPtr.Zero)
        {
            return;
        }

        if (include.Count == 0)
        {
            SetVisible(false);
            return;
        }

        var area = VirtualScreen();
        if (area != _area)
        {
            _area = area;
            SetWindowPos(Handle, IntPtr.Zero, area.Left, area.Top, area.Width, area.Height, NoZOrder | NoActivateFlag);
        }

        var region = CreateRectRgn(0, 0, 0, 0);
        Combine(region, include, RegionOr, area);
        if (exclude is { Count: > 0 })
        {
            Combine(region, exclude, RegionDiff, area);
        }

        // Система забирает регион себе.
        SetWindowRgn(Handle, region, true);
        SetVisible(true);
    }

    private protected override bool OnMessage(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (message == EraseBackground)
        {
            GetClientRect(Handle, out var rect);
            FillRect(wParam, ref rect, _brush);
            result = new IntPtr(1);
            return true;
        }

        if (message == Paint)
        {
            var paint = new byte[72];
            var dc = BeginPaint(Handle, paint);
            GetClientRect(Handle, out var rect);
            FillRect(dc, ref rect, _brush);
            EndPaint(Handle, paint);
            return true;
        }

        return false;
    }

    private protected override void OnDestroyed() => DeleteObject(_brush);

    private static void Combine(IntPtr region, IReadOnlyCollection<ScreenRect> rects, int mode, ScreenRect area)
    {
        foreach (var rect in rects)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            var part = CreateRectRgn(rect.Left - area.Left, rect.Top - area.Top, rect.Right - area.Left, rect.Bottom - area.Top);
            CombineRgn(region, region, part, mode);
            DeleteObject(part);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr window, uint key, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr dc, ref NativeRect rect, IntPtr brush);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr window, byte[] paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr window, byte[] paint);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

/// <summary>
/// Пиксельная картинка с прозрачностью в своём окне. Картинка увеличивается в целое число раз без сглаживания.
/// Клики по прозрачным точкам проходят к окнам под картинкой.
/// </summary>
public sealed class SpriteOverlay : OverlayWindow
{
    private const int LeftButtonDown = 0x0201;
    private const int LeftButtonUp = 0x0202;
    private const int MouseMoveMessage = 0x0200;
    private const int CaptureChanged = 0x0215;
    private const uint UpdateAlpha = 0x00000002;
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivateFlag = 0x0010;

    private readonly Action<int, int>? _click;
    private readonly Action<SpritePointer, int, int>? _pointer;
    private int _scale = 1;
    private bool _captured;

    internal SpriteOverlay(OverlayThread owner, Action<int, int>? click, Action<SpritePointer, int, int>? pointer = null)
        : base(owner, clickThrough: click is null && pointer is null)
    {
        _click = click;
        _pointer = pointer;
    }

    /// <summary>Левый верхний угол в пикселях экрана.</summary>
    public ScreenPoint Position { get; private set; }

    /// <summary>Размер на экране.</summary>
    public int Width { get; private set; }

    public int Height { get; private set; }

    public ScreenRect Bounds => new(Position.X, Position.Y, Width, Height);

    /// <summary>
    /// Показывает картинку <paramref name="argb"/> (строки сверху вниз, 0xAARRGGBB) размером
    /// <paramref name="width"/>×<paramref name="height"/>, увеличенную в <paramref name="scale"/> раз.
    /// </summary>
    public void Show(uint[] argb, int width, int height, int scale, ScreenPoint position)
    {
        if (Handle == IntPtr.Zero)
        {
            return;
        }

        scale = Math.Max(1, scale);
        _scale = scale;
        Position = position;
        Width = width * scale;
        Height = height * scale;
        var pixels = new uint[Width * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                pixels[(y * Width) + x] = Premultiply(argb[((y / scale) * width) + (x / scale)]);
            }
        }

        var header = new BitmapInfo { Size = 40, Width = Width, Height = -Height, Planes = 1, BitCount = 32 };
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            Marshal.Copy(Array.ConvertAll(pixels, pixel => unchecked((int)pixel)), 0, bits, pixels.Length);
            var previous = SelectObject(memory, bitmap);
            var destination = new NativePoint { X = position.X, Y = position.Y };
            var size = new NativeSize { Width = Width, Height = Height };
            var source = default(NativePoint);
            var blend = new Blend { Operation = 0, Flags = 0, Alpha = 255, Format = 1 };
            UpdateLayeredWindow(Handle, screen, ref destination, ref size, memory, ref source, 0, ref blend, UpdateAlpha);
            SelectObject(memory, previous);
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }

        SetVisible(true);
    }

    /// <summary>Двигает показанную картинку.</summary>
    public void MoveTo(ScreenPoint position)
    {
        if (Handle == IntPtr.Zero || position == Position)
        {
            return;
        }

        Position = position;
        SetWindowPos(Handle, IntPtr.Zero, position.X, position.Y, 0, 0, NoSize | NoZOrder | NoActivateFlag);
    }

    public void Hide() => SetVisible(false);

    private protected override bool OnMessage(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        var value = lParam.ToInt64();
        var x = (short)(value & 0xFFFF);
        var y = (short)((value >> 16) & 0xFFFF);
        if (message == LeftButtonDown && _click is not null)
        {
            _click(x / _scale, y / _scale);
            return true;
        }

        if (_pointer is null)
        {
            return false;
        }

        // Округление вниз и для отрицательных точек: окно захватило мышь, и она может уйти левее картинки.
        var px = (int)Math.Floor(x / (double)_scale);
        var py = (int)Math.Floor(y / (double)_scale);
        switch (message)
        {
            case LeftButtonDown:
                _captured = true;
                SetCapture(Handle);
                _pointer(SpritePointer.Down, px, py);
                return true;
            case MouseMoveMessage when _captured:
                _pointer(SpritePointer.Move, px, py);
                return true;
            case LeftButtonUp when _captured:
                _captured = false;
                ReleaseCapture();
                _pointer(SpritePointer.Up, px, py);
                return true;
            case CaptureChanged when _captured:
                // Захват забрала система (например, Alt+Tab): кнопка считается отпущенной.
                _captured = false;
                _pointer(SpritePointer.Up, px, py);
                return true;
            default:
                return false;
        }
    }

    private static uint Premultiply(uint argb)
    {
        var alpha = argb >> 24;
        if (alpha == 255)
        {
            return argb;
        }

        var red = ((argb >> 16) & 0xFF) * alpha / 255;
        var green = ((argb >> 8) & 0xFF) * alpha / 255;
        var blue = (argb & 0xFF) * alpha / 255;
        return (alpha << 24) | (red << 16) | (green << 8) | blue;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref NativePoint destination, ref NativeSize size, IntPtr source, ref NativePoint sourcePoint, uint key, ref Blend blend, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blend
    {
        public byte Operation;
        public byte Flags;
        public byte Alpha;
        public byte Format;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }
}

/// <summary>Что сделала левая кнопка над картинкой с <see cref="OverlayThread.CreatePointerSprite"/>.</summary>
public enum SpritePointer
{
    Down,
    Move,
    Up,
}
