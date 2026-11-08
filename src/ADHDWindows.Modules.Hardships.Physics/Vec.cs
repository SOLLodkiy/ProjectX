using ADHDWindows.Core.Display;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Physics;

/// <summary>Точка или вектор на экране в дробных пикселях.</summary>
public readonly record struct Vec(double X, double Y)
{
    public static readonly Vec Zero = new(0, 0);

    public double Length => Math.Sqrt((X * X) + (Y * Y));

    public Vec Normalized => Length < 1e-9 ? Zero : this / Length;

    public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y);

    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y);

    public static Vec operator -(Vec a) => new(-a.X, -a.Y);

    public static Vec operator *(Vec a, double k) => new(a.X * k, a.Y * k);

    public static Vec operator /(Vec a, double k) => new(a.X / k, a.Y / k);

    public static double Dot(Vec a, Vec b) => (a.X * b.X) + (a.Y * b.Y);

    /// <summary>Векторное произведение на плоскости: момент силы <paramref name="force"/> на плече <paramref name="arm"/>.</summary>
    public static double Cross(Vec arm, Vec force) => (arm.X * force.Y) - (arm.Y * force.X);

    /// <summary>Поворот на угол в радианах.</summary>
    public Vec Rotate(double angle)
    {
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        return new Vec((X * cos) - (Y * sin), (X * sin) + (Y * cos));
    }

    public ScreenPoint ToPoint() => new((int)Math.Round(X), (int)Math.Round(Y));

    public static Vec From(ScreenPoint point) => new(point.X, point.Y);

    public static Vec FromAngle(double angle) => new(Math.Cos(angle), Math.Sin(angle));
}

/// <summary>Где действуют усложнения с физикой.</summary>
public static class PhysicsArea
{
    /// <summary>Все мониторы вместе; в автотестах — только тестовый монитор.</summary>
    public static ScreenRect All() => RegionOverlay.VirtualScreen();

    /// <summary>Каждый монитор отдельно; в автотестах — только тестовый.</summary>
    public static IReadOnlyList<ScreenRect> Screens() =>
        TestScreen.Find() is { } test ? [test.Bounds] : [.. Desktop.Screens().Select(screen => screen.Bounds)];

    /// <summary>Держит точку внутри прямоугольника.</summary>
    public static Vec Clamp(ScreenRect area, Vec point) =>
        new(Math.Clamp(point.X, area.Left, area.Right - 1), Math.Clamp(point.Y, area.Top, area.Bottom - 1));
}
