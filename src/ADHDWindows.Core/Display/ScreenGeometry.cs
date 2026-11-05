namespace ADHDWindows.Core.Display;

/// <summary>Точка в пикселях экрана общего рабочего стола.</summary>
public readonly record struct ScreenPoint(int X, int Y);

/// <summary>Прямоугольник в пикселях экрана общего рабочего стола.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;

    public bool Contains(ScreenPoint point) =>
        point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;

    public bool Contains(ScreenRect other) =>
        other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;

    /// <summary>Площадь пересечения.</summary>
    public long Overlap(ScreenRect other)
    {
        long width = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
        long height = Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top);
        return width > 0 && height > 0 ? width * height : 0;
    }

    /// <summary>Квадрат расстояния между центрами.</summary>
    public long DistanceTo(ScreenRect other)
    {
        long x = (Left + (Width / 2)) - (other.Left + (other.Width / 2));
        long y = (Top + (Height / 2)) - (other.Top + (other.Height / 2));
        return (x * x) + (y * y);
    }
}

/// <summary>Монитор: <paramref name="WorkArea"/> — его часть без панели задач.</summary>
public sealed record ScreenArea(ScreenRect Bounds, ScreenRect WorkArea, int Dpi, bool IsPrimary);
