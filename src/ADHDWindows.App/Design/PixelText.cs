using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using ADHDWindows.Core.Pixels;

namespace ADHDWindows.App.Design;

/// <summary>
/// Текст растровым шрифтом. Символы рисуются прямоугольниками по пикселям, поэтому WPF не сглаживает их.
/// </summary>
public class PixelText : FrameworkElement
{
    private const int LineSpacing = 1;

    private const FrameworkPropertyMetadataOptions Layout =
        FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(PixelText), new FrameworkPropertyMetadata("", Layout));

    public static readonly DependencyProperty FontRoleProperty = DependencyProperty.Register(
        nameof(FontRole), typeof(PixelFontRole), typeof(PixelText), new FrameworkPropertyMetadata(PixelFontRole.Body, Layout));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(int), typeof(PixelText), new FrameworkPropertyMetadata(1, Layout));

    public static readonly DependencyProperty WrapProperty = DependencyProperty.Register(
        nameof(Wrap), typeof(bool), typeof(PixelText), new FrameworkPropertyMetadata(false, Layout));

    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
        nameof(TextAlignment),
        typeof(TextAlignment),
        typeof(PixelText),
        new FrameworkPropertyMetadata(TextAlignment.Left, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShadowProperty = DependencyProperty.Register(
        nameof(Shadow),
        typeof(bool),
        typeof(PixelText),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(PixelText),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly PixelSnap _snap;
    private IReadOnlyList<string> _lines = [];

    public PixelText()
    {
        _snap = new PixelSnap(this);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public PixelFontRole FontRole
    {
        get => (PixelFontRole)GetValue(FontRoleProperty);
        set => SetValue(FontRoleProperty, value);
    }

    public int Zoom
    {
        get => (int)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public bool Wrap
    {
        get => (bool)GetValue(WrapProperty);
        set => SetValue(WrapProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public bool Shadow
    {
        get => (bool)GetValue(ShadowProperty);
        set => SetValue(ShadowProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private PixelFont Font => PixelAssets.Current.Font(FontRole);

    private int LineStep => Font.LineHeight + LineSpacing;

    protected override Size MeasureOverride(Size availableSize)
    {
        var font = Font;
        var limit = Wrap && !double.IsInfinity(availableSize.Width) ? (int)(availableSize.Width / Zoom) : 0;
        _lines = font.Wrap(Text ?? "", limit);
        var width = _lines.Count == 0 ? 0 : _lines.Max(line => font.MeasureWidth(line));
        var height = (_lines.Count * LineStep) - LineSpacing;
        return new Size(width * Zoom, height * Zoom);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var geometry = BuildGeometry();
        var offset = _snap.Take();
        if (Shadow)
        {
            drawing.PushTransform(new TranslateTransform(offset.X, offset.Y + Zoom));
            drawing.DrawGeometry(PixelAssets.Current.Brush("ink"), null, geometry);
            drawing.Pop();
        }

        drawing.PushTransform(new TranslateTransform(offset.X, offset.Y));
        drawing.DrawGeometry(Foreground, null, geometry);
        drawing.Pop();
    }

    private StreamGeometry BuildGeometry()
    {
        var font = Font;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var index = 0; index < _lines.Count; index++)
            {
                var penX = LineStart(font.MeasureWidth(_lines[index]));
                var penY = index * LineStep;
                foreach (var rune in _lines[index].EnumerateRunes())
                {
                    var glyph = font.Glyph(rune.Value);
                    AddGlyph(context, glyph, penX + glyph.OffsetX, penY + glyph.OffsetY);
                    penX += glyph.Advance;
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private int LineStart(int lineWidth)
    {
        var free = (int)Math.Round(ActualWidth / Zoom) - lineWidth;
        return TextAlignment switch
        {
            TextAlignment.Center => free / 2,
            TextAlignment.Right => free,
            _ => 0,
        };
    }

    private void AddGlyph(StreamGeometryContext context, PixelGlyph glyph, int left, int top)
    {
        for (var row = 0; row < glyph.Rows.Count; row++)
        {
            var line = glyph.Rows[row];
            var column = 0;
            while (column < line.Length)
            {
                if (line[column] != '#')
                {
                    column++;
                    continue;
                }

                var start = column;
                while (column < line.Length && line[column] == '#')
                {
                    column++;
                }

                AddRectangle(context, left + start, top + row, column - start);
            }
        }
    }

    private void AddRectangle(StreamGeometryContext context, int x, int y, int width)
    {
        var left = (double)x * Zoom;
        var top = (double)y * Zoom;
        var right = left + (width * Zoom);
        var bottom = top + Zoom;
        context.BeginFigure(new Point(left, top), isFilled: true, isClosed: true);
        context.LineTo(new Point(right, top), isStroked: false, isSmoothJoin: false);
        context.LineTo(new Point(right, bottom), isStroked: false, isSmoothJoin: false);
        context.LineTo(new Point(left, bottom), isStroked: false, isSmoothJoin: false);
    }
}
