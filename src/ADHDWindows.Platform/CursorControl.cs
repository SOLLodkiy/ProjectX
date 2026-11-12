using System.ComponentModel;
using System.Runtime.InteropServices;
using ADHDWindows.Core.Display;

namespace ADHDWindows.Platform;

/// <summary>Положение, скорость и вид системного курсора.</summary>
public static class CursorControl
{
    private const int LeftButton = 0x01;
    private const int RightButton = 0x02;
    private const int MiddleButton = 0x04;

    private const uint GetMouseSpeed = 0x0070;
    private const uint SetMouseSpeed = 0x0071;
    private const uint ReloadCursors = 0x0057;

    public const int MinSpeed = 1;
    public const int MaxSpeed = 20;
    public const int DefaultSpeed = 10;

    // Стандартные курсоры системы (OCR_*): стрелка, текст, ожидание, перекрестие, стрелка вверх, размеры, перемещение,
    // запрет, фоновая работа, рука, помощь.
    private static readonly uint[] AllCursors = [32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32650, 32649, 32651];

    /// <summary>Стрелка: её видно чаще всего.</summary>
    public const uint ArrowCursor = 32512;

    // Размер BITMAPINFOHEADER без таблицы цветов.
    private const int HeaderSize = 40;

    public static ScreenPoint Position() => Desktop.CursorPosition();

    /// <summary>Ставит курсор в точку. Система сама удерживает его в пределах мониторов.</summary>
    public static void MoveTo(ScreenPoint point) => SetCursorPos(point.X, point.Y);

    /// <summary>Нажата ли сейчас какая-нибудь кнопка мыши: в это время курсор не трогают, чтобы не испортить перетаскивание.</summary>
    public static bool AnyButtonDown() =>
        (GetAsyncKeyState(LeftButton) & 0x8000) != 0
        || (GetAsyncKeyState(RightButton) & 0x8000) != 0
        || (GetAsyncKeyState(MiddleButton) & 0x8000) != 0;

    /// <summary>Скорость указателя из настроек мыши, от 1 до 20.</summary>
    public static int Speed()
    {
        var speed = 0;
        return SystemParametersInfoW(GetMouseSpeed, 0, ref speed, 0) ? Math.Clamp(speed, MinSpeed, MaxSpeed) : DefaultSpeed;
    }

    /// <summary>Меняет скорость указателя до перезагрузки, не записывая её в профиль пользователя.</summary>
    public static void SetSpeed(int speed)
    {
        if (!SystemParametersInfoW(SetMouseSpeed, 0, new IntPtr(Math.Clamp(speed, MinSpeed, MaxSpeed)), 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>
    /// Заменяет курсоры системы картинкой <paramref name="argb"/> (строки сверху вниз, 0xAARRGGBB).
    /// Отменяется только <see cref="RestoreCursors"/>, поэтому изменение нужно записать в реестр изменений до вызова.
    /// </summary>
    public static void ReplaceCursors(uint[] argb, int size, int hotX, int hotY, bool allKinds) =>
        ReplaceCursors(argb, size, hotX, hotY, allKinds ? AllCursors : [ArrowCursor]);

    /// <summary>Курсоры-указатели: стрелка, рука над ссылкой и стрелка с фоновой работой. Текстовый курсор и стрелки размеров не трогаются.</summary>
    public static readonly IReadOnlyList<uint> PointerCursors = [ArrowCursor, 32649, 32650];

    /// <summary>Заменяет только курсоры <paramref name="kinds"/> (OCR_*). Отменяется <see cref="RestoreCursors"/>.</summary>
    public static void ReplaceCursors(uint[] argb, int size, int hotX, int hotY, IReadOnlyList<uint> kinds)
    {
        foreach (var kind in kinds)
        {
            // Система забирает переданный курсор себе, поэтому на каждый вид нужен свой.
            var cursor = CreateCursor(argb, size, hotX, hotY);
            if (!SetSystemCursor(cursor, kind))
            {
                var error = Marshal.GetLastWin32Error();
                DestroyIcon(cursor);
                throw new Win32Exception(error);
            }
        }
    }

    /// <summary>Возвращает курсоры из схемы пользователя. Повторный вызов безопасен.</summary>
    public static void RestoreCursors()
    {
        if (!SystemParametersInfoW(ReloadCursors, 0, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Размер курсора в системе: картинки рисуются с целым увеличением под него.</summary>
    public static int SystemCursorSize() => Math.Max(32, GetSystemMetrics(13));

    /// <summary>
    /// Видимые точки системной стрелки, для проверок. Общий описатель стрелки после замены показывает новую картинку,
    /// поэтому ответ не зависит от того, что сейчас под курсором.
    /// </summary>
    public static int VisiblePixelsOfArrow()
    {
        var arrow = LoadCursorW(IntPtr.Zero, new IntPtr(ArrowCursor));
        if (arrow == IntPtr.Zero || !GetIconInfo(arrow, out var icon))
        {
            return 0;
        }

        try
        {
            if (icon.Color != IntPtr.Zero)
            {
                return CountBits(icon.Color, 32, value => (value & 0xFF000000) != 0 || (value & 0x00FFFFFF) != 0);
            }

            // Чёрно-белый курсор: верхняя половина маски — AND, нижняя — XOR. Видна точка, где AND = 0 или XOR = 1.
            return CountMonochrome(icon.Mask);
        }
        finally
        {
            if (icon.Mask != IntPtr.Zero)
            {
                DeleteObject(icon.Mask);
            }

            if (icon.Color != IntPtr.Zero)
            {
                DeleteObject(icon.Color);
            }
        }
    }

    /// <summary>
    /// Картинка системной стрелки, как её сейчас видит человек (с его схемой и размером): 0xAARRGGBB, строки сверху вниз.
    /// Null — картинку прочитать не удалось.
    /// </summary>
    public static CursorImage? ArrowImage()
    {
        var arrow = LoadCursorW(IntPtr.Zero, new IntPtr(ArrowCursor));
        if (arrow == IntPtr.Zero || !GetIconInfo(arrow, out var icon))
        {
            return null;
        }

        try
        {
            return icon.Color != IntPtr.Zero
                ? ReadColor(icon.Color, icon.Mask, icon.HotX, icon.HotY)
                : ReadMonochrome(icon.Mask, icon.HotX, icon.HotY);
        }
        finally
        {
            if (icon.Mask != IntPtr.Zero)
            {
                DeleteObject(icon.Mask);
            }

            if (icon.Color != IntPtr.Zero)
            {
                DeleteObject(icon.Color);
            }
        }
    }

    private static CursorImage? ReadColor(IntPtr color, IntPtr mask, int hotX, int hotY)
    {
        if (ReadBits(color, out var width, out var height) is not { } pixels)
        {
            return null;
        }

        // Старые цветные курсоры без альфа-канала: прозрачность берётся из маски.
        if (pixels.All(pixel => (pixel >> 24) == 0))
        {
            var maskBits = ReadBits(mask, out _, out var maskHeight);
            for (var i = 0; i < pixels.Length; i++)
            {
                var transparent = maskBits is not null && maskHeight >= height && (maskBits[i] & 0x00FFFFFF) != 0;
                pixels[i] = transparent ? 0 : pixels[i] | 0xFF000000;
            }
        }

        return new CursorImage(pixels, width, height, hotX, hotY);
    }

    private static CursorImage? ReadMonochrome(IntPtr mask, int hotX, int hotY)
    {
        if (ReadBits(mask, out var width, out var doubled) is not { } bits)
        {
            return null;
        }

        // Верхняя половина маски — AND, нижняя — XOR. Инвертирующие точки рисуются чёрными.
        var height = doubled / 2;
        var pixels = new uint[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            var and = (bits[i] & 0x00FFFFFF) != 0;
            var xor = (bits[i + pixels.Length] & 0x00FFFFFF) != 0;
            pixels[i] = (and, xor) switch
            {
                (false, true) => 0xFFFFFFFF,
                (false, false) => 0xFF000000,
                (true, true) => 0xFF000000,
                _ => 0,
            };
        }

        return new CursorImage(pixels, width, height, hotX, hotY);
    }

    private static uint[]? ReadBits(IntPtr bitmap, out int width, out int height)
    {
        width = 0;
        height = 0;
        var header = new BitmapHeader { Size = HeaderSize };
        var screen = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(screen, bitmap, 0, 0, null, ref header, 0) == 0)
            {
                return null;
            }

            width = header.Width;
            height = Math.Abs(header.Height);
            header.BitCount = 32;
            header.Compression = 0;
            header.Height = -height;
            var pixels = new uint[width * height];
            return GetDIBits(screen, bitmap, 0, (uint)height, pixels, ref header, 0) == 0 ? null : pixels;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static IntPtr CreateCursor(uint[] argb, int size, int hotX, int hotY)
    {
        var maskStride = ((size + 15) / 16) * 2;
        var mask = new byte[maskStride * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if ((argb[(y * size) + x] >> 24) == 0)
                {
                    mask[(y * maskStride) + (x / 8)] |= (byte)(0x80 >> (x % 8));
                }
            }
        }

        var color = CreateBitmap(size, size, 1, 32, argb);
        var maskBitmap = CreateBitmap(size, size, 1, 1, mask);
        try
        {
            var info = new IconInfo { IsIcon = false, HotX = hotX, HotY = hotY, Mask = maskBitmap, Color = color };
            var cursor = CreateIconIndirect(ref info);
            if (cursor == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return cursor;
        }
        finally
        {
            DeleteObject(color);
            DeleteObject(maskBitmap);
        }
    }

    private static int CountBits(IntPtr bitmap, int bits, Func<uint, bool> visible)
    {
        var header = new BitmapHeader { Size = HeaderSize };
        var screen = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(screen, bitmap, 0, 0, null, ref header, 0) == 0)
            {
                return 0;
            }

            var height = Math.Abs(header.Height);
            header.BitCount = (ushort)bits;
            header.Compression = 0;
            header.Height = -height;
            var pixels = new uint[header.Width * height];
            if (GetDIBits(screen, bitmap, 0, (uint)height, pixels, ref header, 0) == 0)
            {
                return 0;
            }

            return pixels.Count(visible);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static int CountMonochrome(IntPtr mask)
    {
        var header = new BitmapHeader { Size = HeaderSize };
        var screen = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(screen, mask, 0, 0, null, ref header, 0) == 0)
            {
                return 0;
            }

            var width = header.Width;
            var height = Math.Abs(header.Height) / 2;
            header.BitCount = 32;
            header.Compression = 0;
            header.Height = -height * 2;
            var pixels = new uint[width * height * 2];
            if (GetDIBits(screen, mask, 0, (uint)(height * 2), pixels, ref header, 0) == 0)
            {
                return 0;
            }

            var count = 0;
            for (var i = 0; i < width * height; i++)
            {
                var and = (pixels[i] & 0x00FFFFFF) != 0;
                var xor = (pixels[i + (width * height)] & 0x00FFFFFF) != 0;
                if (!and || xor)
                {
                    count++;
                }
            }

            return count;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint parameter, ref int value, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint parameter, IntPtr value, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSystemCursor(IntPtr cursor, uint id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref IconInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, uint[] bits);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte[] bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, uint[]? bits, ref BitmapHeader header, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool IsIcon;
        public int HotX;
        public int HotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    // BITMAPINFOHEADER с запасом под таблицу цветов монохромной картинки.
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
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
        public uint Color0;
        public uint Color1;
    }
}

/// <summary>Картинка курсора: точки 0xAARRGGBB строками сверху вниз и горячая точка.</summary>
public sealed record CursorImage(uint[] Pixels, int Width, int Height, int HotX, int HotY);
