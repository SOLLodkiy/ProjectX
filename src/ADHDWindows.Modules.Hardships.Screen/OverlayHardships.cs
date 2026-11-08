using ADHDWindows.Core.Display;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Screen;

/// <summary>
/// Усложнение, которое рисует поверх экрана в своём потоке окон. Окна принадлежат программе и исчезают вместе
/// с ней, поэтому записывать в реестр изменений нечего.
/// </summary>
public abstract class OverlayHardship : HardshipModule
{
    private const int LoggedErrors = 3;

    private OverlayThread? _overlay;
    private int _errors;

    /// <summary>Сколько окон оверлея сейчас открыто, для проверок.</summary>
    internal int OverlayWindows => _overlay?.WindowCount ?? 0;

    /// <summary>Поток окон, пока усложнение работает.</summary>
    protected OverlayThread Overlay => _overlay ?? throw new InvalidOperationException("Оверлей не запущен");

    protected override void Begin()
    {
        _errors = 0;
        _overlay = new OverlayThread(Info.Id, Tick, OnError);
        _overlay.Invoke(Create);
    }

    protected override void End()
    {
        // Окна закрываются вместе с потоком.
        _overlay?.Dispose();
        _overlay = null;
        Finish();
    }

    /// <summary>Создаёт окна. Вызывается в потоке оверлея.</summary>
    protected abstract void Create();

    /// <summary>Шаг анимации. Вызывается в потоке оверлея примерно 60 раз в секунду.</summary>
    protected abstract void Tick(double seconds);

    /// <summary>Итоги после снятия, например запись в журнал.</summary>
    protected virtual void Finish()
    {
    }

    private void OnError(Exception exception)
    {
        // Ошибка в каждом кадре заполнила бы журнал за секунды.
        if (Interlocked.Increment(ref _errors) <= LoggedErrors)
        {
            Context.Log.Error($"Ошибка усложнения {Info.Id}", exception);
        }
    }
}

/// <summary>Оверлей из одного сплошного окна, форма которого меняется каждый кадр.</summary>
public abstract class RegionHardship(uint rgb, byte alpha) : OverlayHardship
{
    private readonly List<ScreenRect> _include = [];
    private readonly List<ScreenRect> _exclude = [];
    private RegionOverlay? _window;

    protected override void Create() => _window = Overlay.CreateRegion(rgb, alpha);

    protected override void Tick(double seconds)
    {
        _include.Clear();
        _exclude.Clear();
        Shape(seconds, RegionOverlay.VirtualScreen(), _include, _exclude);
        _window?.SetShape(_include, _exclude);
    }

    /// <summary>Заполняет форму окна: что закрашено и какие дыры в нём.</summary>
    protected abstract void Shape(double seconds, ScreenRect area, List<ScreenRect> include, List<ScreenRect> exclude);
}

/// <summary>Пиксельный дождь: мелкие капли падают по экрану и не мешают кликам.</summary>
public sealed class RainHardship() : RegionHardship(0x4FB3FF, 150)
{
    private FallingField? _field;

    public override HardshipInfo Info { get; } = new("pixelRain", HardshipLevel.Easy, HardshipResources.Screen);

    protected override void Shape(double seconds, ScreenRect area, List<ScreenRect> include, List<ScreenRect> exclude)
    {
        _field ??= new FallingField(new Random(), FallSettings.Rain, area);
        _field.Resize(area);
        _field.Step(seconds, include);
    }

    protected override void Finish() => _field = null;
}

/// <summary>Снегопад: крупные пиксельные снежинки медленно падают и покачиваются.</summary>
public sealed class SnowHardship() : RegionHardship(0xFFFFFF, 220)
{
    private FallingField? _field;

    public override HardshipInfo Info { get; } = new("snow", HardshipLevel.Easy, HardshipResources.Screen);

    protected override void Shape(double seconds, ScreenRect area, List<ScreenRect> include, List<ScreenRect> exclude)
    {
        _field ??= new FallingField(new Random(), FallSettings.Snow, area);
        _field.Resize(area);
        _field.Step(seconds, include);
    }

    protected override void Finish() => _field = null;
}

/// <summary>Монетная пыль: за курсором летят золотые пиксели.</summary>
public sealed class CoinDustHardship() : RegionHardship(0xFFCF4A, 255)
{
    private CoinDust? _dust;

    public override HardshipInfo Info { get; } = new("coinDust", HardshipLevel.Easy, HardshipResources.Screen);

    protected override void Shape(double seconds, ScreenRect area, List<ScreenRect> include, List<ScreenRect> exclude)
    {
        _dust ??= new CoinDust(new Random());
        _dust.Step(seconds, CursorControl.Position(), include);
    }

    protected override void Finish() => _dust = null;
}

/// <summary>Сумерки: весь экран заметно темнее.</summary>
public sealed class TwilightHardship() : RegionHardship(0x14121F, 120)
{
    public override HardshipInfo Info { get; } = new("twilight", HardshipLevel.Easy, HardshipResources.Screen);

    protected override void Shape(double seconds, ScreenRect area, List<ScreenRect> include, List<ScreenRect> exclude) =>
        include.Add(area);
}

/// <summary>Туман: экран затянут, кроме области вокруг курсора.</summary>
public sealed class FogHardship() : RegionHardship(0xA89FC4, 242)
{
    public const int Radius = 220;
    public const int Block = 8;

    public override HardshipInfo Info { get; } = new("fog", HardshipLevel.Medium, HardshipResources.Screen);

    protected override void Shape(double seconds, ScreenRect area, List<ScreenRect> include, List<ScreenRect> exclude)
    {
        include.Add(area);
        exclude.AddRange(PixelShapes.Circle(CursorControl.Position(), Radius, Block));
    }
}

/// <summary>Темнота: видно только маленький круг вокруг курсора.</summary>
public sealed class DarknessHardship() : RegionHardship(0x14121F, 250)
{
    public const int Radius = 110;
    public const int Block = 6;

    public override HardshipInfo Info { get; } = new("darkness", HardshipLevel.Hard, HardshipResources.Screen);

    protected override void Shape(double seconds, ScreenRect area, List<ScreenRect> include, List<ScreenRect> exclude)
    {
        include.Add(area);
        exclude.AddRange(PixelShapes.Circle(CursorControl.Position(), Radius, Block));
    }
}
