using ADHDWindows.Core.Display;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Pixels;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Screen;

/// <summary>
/// Реклама: появляются пиксельные окна-рекламы, их нужно закрыть крестиком. Окна не забирают фокус,
/// держатся ниже окон ADHDWindows и все исчезают после усложнения.
/// </summary>
public sealed class AdsHardship : OverlayHardship
{
    public const int MaxAds = 3;
    public const double MinPause = 6;
    public const double MaxPause = 12;
    public const int Scale = 2;

    /// <summary>Ширина окна рекламы в пикселях арта.</summary>
    public const int ArtWidth = 120;

    private const int TitleHeight = 13;
    private const int CloseSize = 11;
    private const int Padding = 5;

    private readonly Random _random = new();
    private readonly List<SpriteOverlay> _ads = [];
    private readonly Lock _lock = new();
    private IReadOnlyList<string> _offers = [];
    private PixelFont? _font;
    private double _nextIn;
    private int _closed;

    public override HardshipInfo Info { get; } = new("ads", HardshipLevel.Medium, HardshipResources.Screen);

    /// <summary>Сколько реклам открыто сейчас, для проверок.</summary>
    internal int Open => Overlay.Invoke(() => _ads.Count);

    /// <summary>Сколько реклам закрыто крестиком.</summary>
    internal int Closed
    {
        get
        {
            lock (_lock)
            {
                return _closed;
            }
        }
    }

    /// <summary>Нарисованное окно рекламы с текстом <paramref name="offer"/>. Крестик — в правом верхнем углу.</summary>
    public static Picture Draw(string offer, PixelFont? font)
    {
        var lines = font?.Wrap(offer, ArtWidth - (Padding * 2)) ?? [];
        var lineHeight = font?.LineHeight ?? 12;
        var height = TitleHeight + Padding + (Math.Max(2, lines.Count) * lineHeight) + Padding + 12 + Padding;
        var picture = new Picture(ArtWidth, height);

        // Рамка, заголовок и тело.
        picture.Fill(0, 0, ArtWidth, height, Colors.Ink);
        picture.Fill(1, 1, ArtWidth - 2, TitleHeight - 1, Colors.Pink);
        picture.Fill(1, TitleHeight, ArtWidth - 2, height - TitleHeight - 1, Colors.Panel);

        // Крестик.
        var closeX = ArtWidth - CloseSize - 1;
        picture.Fill(closeX, 1, CloseSize, CloseSize, Colors.RedDark);
        for (var i = 2; i < CloseSize - 2; i++)
        {
            picture[closeX + i, 1 + i] = Colors.White;
            picture[closeX + CloseSize - 1 - i, 1 + i] = Colors.White;
        }

        // Кнопка-приманка внизу.
        var buttonTop = height - Padding - 12;
        picture.Fill(Padding, buttonTop, ArtWidth - (Padding * 2), 12, Colors.Gold);
        picture.Fill(Padding, buttonTop + 11, ArtWidth - (Padding * 2), 1, Colors.Amber);

        if (font is not null)
        {
            OverlayFont.Draw(picture, font, "РЕКЛАМА", 4, 0, Colors.Ink);
            for (var i = 0; i < lines.Count; i++)
            {
                OverlayFont.Draw(picture, font, lines[i], Padding, TitleHeight + Padding + (i * lineHeight), Colors.Text);
            }

            const string button = "ЗАБРАТЬ!";
            OverlayFont.Draw(picture, font, button, (ArtWidth - font.MeasureWidth(button)) / 2, buttonTop - 1, Colors.Ink);
        }

        return picture;
    }

    /// <summary>Попадает ли точка картинки в крестик.</summary>
    public static bool IsClose(int x, int y) => x >= ArtWidth - CloseSize - 1 && y <= CloseSize + 1;

    /// <summary>Закрыть первую рекламу, для проверок: как клик по её крестику.</summary>
    internal void CloseOneForTest() => Overlay.Invoke(() =>
    {
        if (_ads.Count > 0)
        {
            Close(_ads[0]);
        }
    });

    protected override void Create()
    {
        _ads.Clear();
        _closed = 0;
        _font = OverlayFont.Body(Context.Log);
        _offers = Offers();
        Spawn();
        _nextIn = Pause();
    }

    protected override void Tick(double seconds)
    {
        _nextIn -= seconds;
        if (_nextIn > 0)
        {
            return;
        }

        _nextIn = Pause();
        if (_ads.Count < MaxAds)
        {
            Spawn();
        }
    }

    protected override void Finish()
    {
        _ads.Clear();
        lock (_lock)
        {
            Context.Log.Info($"Реклама: закрыто {_closed}");
        }
    }

    private void Spawn()
    {
        var offer = _offers.Count > 0 ? _offers[_random.Next(_offers.Count)] : "???";
        var picture = Draw(offer, _font);
        SpriteOverlay? ad = null;
        ad = Overlay.CreateSprite((x, y) =>
        {
            if (IsClose(x, y))
            {
                Close(ad!);
            }
        });

        var area = OverlayArea.WorkArea();
        var width = picture.Width * Scale;
        var height = picture.Height * Scale;
        var position = new ScreenPoint(
            area.Left + _random.Next(Math.Max(1, area.Width - width)),
            area.Top + _random.Next(Math.Max(1, area.Height - height)));
        ad.Show(picture.Pixels, picture.Width, picture.Height, Scale, position);
        _ads.Add(ad);
    }

    private void Close(SpriteOverlay ad)
    {
        if (!_ads.Remove(ad))
        {
            return;
        }

        ad.Destroy();
        lock (_lock)
        {
            _closed++;
        }
    }

    private List<string> Offers()
    {
        var offers = new List<string>();
        for (var i = 1; i <= 50; i++)
        {
            var key = $"hardships.ads.offer{i}";
            var text = Context.Texts.Get(key);
            if (text == key)
            {
                break;
            }

            offers.Add(text);
        }

        return offers;
    }

    private double Pause() => MinPause + (_random.NextDouble() * (MaxPause - MinPause));
}
