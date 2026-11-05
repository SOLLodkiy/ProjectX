using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ADHDWindows.App.Design;

public enum PixelButtonKind
{
    Normal,
    Primary,
    Danger,
}

/// <summary>Кнопка: рамка выбирается по виду и состоянию.</summary>
public class PixelButton : Button
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(PixelButtonKind),
        typeof(PixelButton),
        new FrameworkPropertyMetadata(PixelButtonKind.Normal, (element, _) => ((PixelButton)element).UpdateSprite()));

    private static readonly DependencyPropertyKey FrameSpritePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(FrameSprite),
        typeof(string),
        typeof(PixelButton),
        new FrameworkPropertyMetadata("button"));

    public static readonly DependencyProperty FrameSpriteProperty = FrameSpritePropertyKey.DependencyProperty;

    public PixelButton()
    {
        IsEnabledChanged += (_, _) => UpdateSprite();
    }

    public PixelButtonKind Kind
    {
        get => (PixelButtonKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string FrameSprite => (string)GetValue(FrameSpriteProperty);

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        // Пиксельный текст не виден специальным возможностям Windows, поэтому имя задаётся явно.
        AutomationProperties.SetName(this, newContent as string ?? "");
    }

    protected override void OnIsPressedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsPressedChanged(e);
        UpdateSprite();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsMouseOverProperty || e.Property == IsKeyboardFocusedProperty)
        {
            UpdateSprite();
        }
    }

    private void UpdateSprite()
    {
        var name = Kind switch
        {
            PixelButtonKind.Primary => "button_primary",
            PixelButtonKind.Danger => "button_danger",
            _ => "button",
        };

        if (!IsEnabled)
        {
            name = "button_disabled";
        }
        else if (IsPressed)
        {
            name += "_down";
        }
        else if (IsMouseOver || IsKeyboardFocused)
        {
            name += "_hover";
        }

        SetValue(FrameSpritePropertyKey, name);
    }
}
