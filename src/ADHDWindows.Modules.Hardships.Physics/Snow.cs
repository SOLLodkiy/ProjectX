using ADHDWindows.Core.Display;

namespace ADHDWindows.Modules.Hardships.Physics;

/// <summary>
/// Сугроб: экран, разбитый на клетки, в каждой есть снег или нет. Снег не появляется снова: его можно только
/// расчистить. Потоками управляет владелец.
/// </summary>
public sealed class SnowField
{
    private readonly bool[] _snow;

    public SnowField(ScreenRect area, int cell)
    {
        Area = area;
        Cell = Math.Max(2, cell);
        Columns = Math.Max(1, (area.Width + Cell - 1) / Cell);
        Rows = Math.Max(1, (area.Height + Cell - 1) / Cell);
        _snow = new bool[Columns * Rows];
        Array.Fill(_snow, true);
        SnowCells = _snow.Length;
    }

    public ScreenRect Area { get; }

    /// <summary>Размер клетки в пикселях экрана.</summary>
    public int Cell { get; }

    public int Columns { get; }

    public int Rows { get; }

    /// <summary>Сколько клеток ещё под снегом.</summary>
    public int SnowCells { get; private set; }

    /// <summary>Растёт при каждом изменении: окно перерисовывается, только когда снег поменялся.</summary>
    public int Version { get; private set; }

    /// <summary>Есть ли снег в точке. Вне сугроба снега нет.</summary>
    public bool IsSnow(double x, double y)
    {
        if (x < Area.Left || y < Area.Top || x >= Area.Right || y >= Area.Bottom)
        {
            return false;
        }

        var column = (int)((x - Area.Left) / Cell);
        var row = (int)((y - Area.Top) / Cell);
        return _snow[(row * Columns) + column];
    }

    public bool IsSnow(Vec point) => IsSnow(point.X, point.Y);

    public bool IsSnow(ScreenPoint point) => IsSnow(point.X, point.Y);

    /// <summary>Расчищает круг: середина клетки внутри круга. Возвращает число расчищенных клеток.</summary>
    public int ClearCircle(Vec center, double radius) =>
        Clear(center, radius, radius, (x, y) => Square(x - center.X) + Square(y - center.Y) <= radius * radius);

    /// <summary>
    /// Расчищает повёрнутый прямоугольник с серединой <paramref name="center"/>, полуразмерами вдоль осей
    /// <paramref name="axis"/> и перпендикулярно ей. Возвращает число расчищенных клеток.
    /// </summary>
    public int ClearBox(Vec center, Vec axis, double halfLength, double halfWidth)
    {
        var along = axis.Normalized;
        var across = new Vec(-along.Y, along.X);
        var reach = Math.Sqrt((halfLength * halfLength) + (halfWidth * halfWidth));
        return Clear(center, reach, reach, (x, y) =>
        {
            var offset = new Vec(x, y) - center;
            return Math.Abs(Vec.Dot(offset, along)) <= halfLength && Math.Abs(Vec.Dot(offset, across)) <= halfWidth;
        });
    }

    /// <summary>
    /// Куда дойдёт курсор из <paramref name="from"/> в сторону <paramref name="to"/>, не заходя в снег.
    /// Путь проверяется шагами меньше клетки, поэтому быстрое движение не перепрыгивает тонкую стенку.
    /// </summary>
    public Vec Reach(Vec from, Vec to)
    {
        var delta = to - from;
        var length = delta.Length;
        if (length < 1e-6)
        {
            return from;
        }

        var steps = (int)Math.Ceiling(length / Math.Max(1, Cell / 3.0));
        var last = from;
        for (var i = 1; i <= steps; i++)
        {
            var point = from + (delta * (i / (double)steps));
            if (IsSnow(point))
            {
                return last;
            }

            last = point;
        }

        return to;
    }

    /// <summary>Прямоугольники снега для формы окна: полосы по строкам, одинаковые полосы соседних строк склеены.</summary>
    public List<ScreenRect> Rects()
    {
        var rects = new List<ScreenRect>();
        var open = new Dictionary<(int Start, int End), int>();
        for (var row = 0; row < Rows; row++)
        {
            var next = new Dictionary<(int Start, int End), int>();
            var column = 0;
            while (column < Columns)
            {
                if (!_snow[(row * Columns) + column])
                {
                    column++;
                    continue;
                }

                var start = column;
                while (column < Columns && _snow[(row * Columns) + column])
                {
                    column++;
                }

                var key = (start, column);
                if (open.Remove(key, out var index))
                {
                    var rect = rects[index];
                    rects[index] = rect with { Height = rect.Height + Cell };
                    next[key] = index;
                }
                else
                {
                    next[key] = rects.Count;
                    rects.Add(new ScreenRect(Area.Left + (start * Cell), Area.Top + (row * Cell), (column - start) * Cell, Cell));
                }
            }

            open = next;
        }

        return rects;
    }

    private int Clear(Vec center, double reachX, double reachY, Func<double, double, bool> inside)
    {
        var firstColumn = Math.Max(0, (int)Math.Floor((center.X - reachX - Area.Left) / Cell));
        var lastColumn = Math.Min(Columns - 1, (int)Math.Floor((center.X + reachX - Area.Left) / Cell));
        var firstRow = Math.Max(0, (int)Math.Floor((center.Y - reachY - Area.Top) / Cell));
        var lastRow = Math.Min(Rows - 1, (int)Math.Floor((center.Y + reachY - Area.Top) / Cell));
        var cleared = 0;
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var index = (row * Columns) + column;
                if (!_snow[index])
                {
                    continue;
                }

                var x = Area.Left + ((column + 0.5) * Cell);
                var y = Area.Top + ((row + 0.5) * Cell);
                if (inside(x, y))
                {
                    _snow[index] = false;
                    cleared++;
                }
            }
        }

        if (cleared > 0)
        {
            SnowCells -= cleared;
            Version++;
        }

        return cleared;
    }

    private static double Square(double value) => value * value;
}

/// <summary>
/// Лопата: твёрдое тело без гравитации. Её берут за ручку, тянут пружиной за точку хвата, поэтому она вращается
/// и движется по инерции; после броска летит и медленно останавливается. Расчищает снег штыком.
/// В координатах лопаты ось X идёт от ручки к штыку, начало — середина лопаты.
/// </summary>
public sealed class Shovel
{
    public const double Stiffness = 900;
    public const double LinearDrag = 0.9;
    public const double AngularDrag = 1.4;
    public const double MaxSpeed = 3500;
    public const double MaxSpin = 25;

    /// <summary>Сколько скорости забирает каждая расчищенная клетка: в снегу лопата вязнет.</summary>
    public const double SnowDrag = 0.006;

    /// <summary>Запас вокруг ручки, чтобы её было легко взять.</summary>
    public const double GripTolerance = 7;

    private readonly double _mass = 1;
    private readonly double _inertia;
    private Vec? _grip;

    public Shovel(Vec position, double angle, double length = 120, double bladeLength = 40, double bladeWidth = 46, double handleWidth = 8)
    {
        Position = position;
        Angle = angle;
        Length = length;
        BladeLength = bladeLength;
        BladeWidth = bladeWidth;
        HandleWidth = handleWidth;

        // Стержень длины L плюс тяжёлый штык на конце.
        _inertia = (_mass * length * length / 12) + (0.5 * Math.Pow(length / 2, 2));
    }

    public Vec Position { get; private set; }

    public double Angle { get; private set; }

    public Vec Velocity { get; private set; }

    public double Spin { get; private set; }

    public double Length { get; }

    public double BladeLength { get; }

    public double BladeWidth { get; }

    public double HandleWidth { get; }

    public bool IsHeld => _grip is not null;

    /// <summary>Середина штыка на экране.</summary>
    public Vec BladeCenter => Position + new Vec((Length / 2) - (BladeLength / 2), 0).Rotate(Angle);

    /// <summary>Точка экрана в координатах лопаты.</summary>
    public Vec ToLocal(Vec point) => (point - Position).Rotate(-Angle);

    /// <summary>Попадает ли точка в ручку (с небольшим запасом).</summary>
    public bool HitsHandle(Vec point)
    {
        var local = ToLocal(point);
        return local.X >= (-Length / 2) - GripTolerance
            && local.X <= (Length / 2) - BladeLength
            && Math.Abs(local.Y) <= (HandleWidth / 2) + GripTolerance;
    }

    /// <summary>Попадает ли точка в штык.</summary>
    public bool HitsBlade(Vec point)
    {
        var local = ToLocal(point);
        return local.X >= (Length / 2) - BladeLength && local.X <= Length / 2 && Math.Abs(local.Y) <= BladeWidth / 2;
    }

    /// <summary>Взять лопату за точку экрана.</summary>
    public void Grab(Vec point) => _grip = ToLocal(point);

    /// <summary>Отпустить: лопата летит дальше с той скоростью, которая у неё была.</summary>
    public void Release() => _grip = null;

    /// <summary>
    /// Шаг физики. <paramref name="hand"/> — курсор, за который тянут взятую лопату. Штык расчищает снег в
    /// <paramref name="snow"/>; лопата отскакивает от краёв <paramref name="bounds"/>. Возвращает число расчищенных клеток.
    /// </summary>
    public int Step(double seconds, Vec hand, SnowField? snow, ScreenRect bounds)
    {
        var cleared = 0;
        const int substeps = 4;
        var dt = seconds / substeps;
        for (var i = 0; i < substeps; i++)
        {
            if (_grip is { } grip)
            {
                // Пружина с критическим затуханием тянет точку хвата к курсору.
                var arm = grip.Rotate(Angle);
                var point = Position + arm;
                var pointVelocity = Velocity + new Vec(-Spin * arm.Y, Spin * arm.X);
                var force = ((hand - point) * Stiffness) - (pointVelocity * (2 * Math.Sqrt(Stiffness * _mass)));
                Velocity += force * (dt / _mass);
                Spin += Vec.Cross(arm, force) * (dt / _inertia);
                Spin *= Math.Exp(-AngularDrag * 2 * dt);
            }
            else
            {
                Velocity *= Math.Exp(-LinearDrag * dt);
                Spin *= Math.Exp(-AngularDrag * dt);
            }

            if (Velocity.Length > MaxSpeed)
            {
                Velocity = Velocity.Normalized * MaxSpeed;
            }

            Spin = Math.Clamp(Spin, -MaxSpin, MaxSpin);
            Position += Velocity * dt;
            Angle += Spin * dt;
            Bounce(bounds);

            if (snow is not null)
            {
                var hit = snow.ClearBox(BladeCenter, Vec.FromAngle(Angle), BladeLength / 2, BladeWidth / 2);
                if (hit > 0 && _grip is null)
                {
                    var keep = Math.Max(0, 1 - (hit * SnowDrag));
                    Velocity *= keep;
                    Spin *= keep;
                }

                cleared += hit;
            }
        }

        return cleared;
    }

    private void Bounce(ScreenRect bounds)
    {
        var x = Position.X;
        var y = Position.Y;
        var vx = Velocity.X;
        var vy = Velocity.Y;
        if (x < bounds.Left)
        {
            x = bounds.Left;
            vx = Math.Abs(vx) * 0.5;
        }
        else if (x > bounds.Right - 1)
        {
            x = bounds.Right - 1;
            vx = -Math.Abs(vx) * 0.5;
        }

        if (y < bounds.Top)
        {
            y = bounds.Top;
            vy = Math.Abs(vy) * 0.5;
        }
        else if (y > bounds.Bottom - 1)
        {
            y = bounds.Bottom - 1;
            vy = -Math.Abs(vy) * 0.5;
        }

        Position = new Vec(x, y);
        Velocity = new Vec(vx, vy);
    }
}
