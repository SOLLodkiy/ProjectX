using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Threading;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.RandomEvents;

namespace ADHDWindows.App.Events;

/// <summary>
/// Окно события по описанию <see cref="EventDialog"/>: текст, кнопки, обратный отсчёт и колесо фортуны.
/// Окно не забирает фокус, поэтому не мешает печатать. Закрытие крестиком сообщается событию как
/// <see cref="EventDialogChosen.CloseChoice"/>, а закрытие самим событием — нет.
/// </summary>
internal sealed class EventDialogWindow : PixelWindow
{
    // Делится на число столбцов колеса: ячейки остаются на целых пикселях.
    private const int ContentWidth = 232;
    private const int WheelColumns = 4;
    private static readonly TimeSpan SpinStep = TimeSpan.FromMilliseconds(70);
    private const int SpinLaps = 2;

    private readonly TextCatalog _texts;
    private readonly IEconomy _economy;
    private readonly Action<string> _choose;
    private readonly PixelText _text = new() { Wrap = true, Margin = new Thickness(0, 0, 0, 5) };
    private readonly PixelText _timer = new() { Foreground = PixelAssets.Current.Brush("mist"), Margin = new Thickness(0, 4, 0, 0) };
    private readonly UniformGrid _wheel = new() { Columns = WheelColumns, Margin = new Thickness(0, 0, 0, 5) };
    private readonly StackPanel _buttons = new();
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _spinner;
    private int _spinAt;
    private int _spinStepsLeft;
    private int _lastSpin;
    private bool _closedByEvent;

    public EventDialogWindow(TextCatalog texts, IEconomy economy, EventDialog dialog, Action<string> choose)
    {
        _texts = texts;
        _economy = economy;
        _choose = choose;
        ShowActivated = false;
        Topmost = true;
        Dialog = dialog;

        var layout = new StackPanel { Width = ContentWidth };
        layout.Children.Add(_text);
        layout.Children.Add(_wheel);
        layout.Children.Add(_buttons);
        layout.Children.Add(_timer);
        TextElement.SetForeground(layout, PixelAssets.Current.Brush("text"));
        Content = layout;

        _clock = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => UpdateTimer(), Dispatcher);
        _spinner = new DispatcherTimer(SpinStep, DispatcherPriority.Normal, (_, _) => OnSpinTick(), Dispatcher) { IsEnabled = false };
        Closed += (_, _) =>
        {
            _clock.Stop();
            _spinner.Stop();
            if (!_closedByEvent)
            {
                _choose(EventDialogChosen.CloseChoice);
            }
        };
        Update(dialog);
    }

    public EventDialog Dialog { get; private set; }

    /// <summary>Колесо ещё крутится, для проверок.</summary>
    internal bool Spinning => _spinner.IsEnabled;

    /// <summary>Подписи кнопок, для проверок.</summary>
    internal IReadOnlyList<PixelButton> Buttons => [.. _buttons.Children.OfType<PixelButton>()];

    /// <summary>Подсвеченный сектор колеса, для проверок.</summary>
    internal int Highlighted => _spinAt;

    /// <summary>Текст окна, для проверок.</summary>
    internal string Text => _text.Text;

    public void Update(EventDialog dialog)
    {
        Dialog = dialog;
        Title = dialog.Title;
        IconSprite = dialog.Icon;
        BuildWheel(dialog);
        BuildButtons(dialog);
        if (dialog.WheelResult is { } result && dialog.Spin != _lastSpin && dialog.Wheel is { Count: > 0 } wheel)
        {
            // Колесо делает пару кругов и останавливается на выигрыше, а текст с итогом виден после остановки.
            _lastSpin = dialog.Spin;
            _spinStepsLeft = (SpinLaps * wheel.Count) + ((result - _spinAt + wheel.Count) % wheel.Count);
            _text.Text = "…";
            SetButtonsEnabled(false);
            _spinner.Start();
        }
        else if (!_spinner.IsEnabled)
        {
            _text.Text = dialog.Text;
        }

        UpdateTimer();
        _clock.IsEnabled = dialog.Expires is not null;
    }

    /// <summary>Событие закончилось: окно закрывается без ответа событию.</summary>
    public void CloseByEvent()
    {
        _closedByEvent = true;
        Close();
    }

    private void BuildWheel(EventDialog dialog)
    {
        _wheel.Children.Clear();
        _wheel.Visibility = dialog.Wheel is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (dialog.Wheel is not { Count: > 0 } wheel)
        {
            return;
        }

        for (var index = 0; index < wheel.Count; index++)
        {
            _wheel.Children.Add(new PixelFrame
            {
                Sprite = index == _spinAt ? "list_selected" : "inset",
                Padding = new Thickness(2),
                Margin = new Thickness(1),
                Child = new PixelText { Text = wheel[index], TextAlignment = TextAlignment.Center },
            });
        }
    }

    private void BuildButtons(EventDialog dialog)
    {
        _buttons.Children.Clear();
        foreach (var choice in dialog.Choices)
        {
            var text = choice.Price > 0 ? _texts.Get("events.dialog.price", choice.Text, CoinFormat.Short(choice.Price)) : choice.Text;
            var button = new PixelButton
            {
                Content = text,
                Kind = dialog.Choices.Count == 1 ? PixelButtonKind.Primary : PixelButtonKind.Normal,
                Margin = new Thickness(0, 0, 0, 2),
                Focusable = false,
                IsEnabled = choice.Enabled && (choice.Price <= 0 || _economy.Balance >= choice.Price) && !_spinner.IsEnabled,
                Tag = choice,
            };
            var id = choice.Id;
            button.Click += (_, _) => _choose(id);
            _buttons.Children.Add(button);
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        foreach (var button in Buttons)
        {
            var choice = (EventDialogChoice)button.Tag;
            button.IsEnabled = enabled && choice.Enabled && (choice.Price <= 0 || _economy.Balance >= choice.Price);
        }
    }

    private void OnSpinTick()
    {
        if (Dialog.Wheel is not { Count: > 0 } wheel)
        {
            _spinner.Stop();
            return;
        }

        _spinAt = (_spinAt + 1) % wheel.Count;
        _spinStepsLeft--;
        for (var index = 0; index < _wheel.Children.Count; index++)
        {
            ((PixelFrame)_wheel.Children[index]).Sprite = index == _spinAt ? "list_selected" : "inset";
        }

        // К концу колесо замедляется.
        if (_spinStepsLeft < wheel.Count)
        {
            _spinner.Interval = SpinStep * (1 + ((wheel.Count - _spinStepsLeft) / 2.0));
        }

        if (_spinStepsLeft <= 0)
        {
            _spinner.Stop();
            _spinner.Interval = SpinStep;
            _text.Text = Dialog.Text;
            SetButtonsEnabled(true);
        }
    }

    private void UpdateTimer()
    {
        if (Dialog.Expires is not { } expires)
        {
            _timer.Visibility = Visibility.Collapsed;
            return;
        }

        var left = expires - DateTimeOffset.UtcNow;
        _timer.Visibility = Visibility.Visible;
        _timer.Text = _texts.Get("events.dialog.left", Math.Max(0, (int)Math.Ceiling(left.TotalSeconds)));
    }
}
