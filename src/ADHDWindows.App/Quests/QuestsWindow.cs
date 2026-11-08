using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Quests;

namespace ADHDWindows.App.Quests;

/// <summary>
/// Окно заданий: строка на каждую сложность с прогрессом, наградой и временем до смены задания,
/// под ними — сколько заданий выполнено подряд в цепочке.
/// </summary>
internal sealed class QuestsWindow : PixelWindow
{
    private const int ContentWidth = 240;

    private readonly Dictionary<QuestTier, QuestRow> _rows = [];
    private readonly TextCatalog _texts;
    private readonly PixelText _chain = new() { Margin = new Thickness(1, 1, 0, 0) };

    public QuestsWindow(TextCatalog texts)
    {
        _texts = texts;
        Title = texts.Get("quests.title");
        IconSprite = "quest";

        var layout = new StackPanel { Width = ContentWidth };
        foreach (var tier in Enum.GetValues<QuestTier>())
        {
            var row = new QuestRow(texts, tier);
            _rows[tier] = row;
            layout.Children.Add(row);
        }

        AutomationProperties.SetAutomationId(_chain, "quests.chain");
        layout.Children.Add(_chain);

        TextElement.SetForeground(layout, PixelAssets.Current.Brush("text"));
        Content = layout;
    }

    /// <summary>Строки по сложностям, для проверок.</summary>
    internal IReadOnlyDictionary<QuestTier, QuestRow> Rows => _rows;

    /// <summary>Строка цепочки, для проверок.</summary>
    internal string ChainText => _chain.Text;

    public void Update(IReadOnlyList<QuestView> quests, DateTimeOffset now, int chain = 0, int chainGoal = 3)
    {
        foreach (var (tier, row) in _rows)
        {
            row.Show(quests.FirstOrDefault(quest => quest.Tier == tier), now);
        }

        _chain.Text = _texts.Get("quests.chain", chain, chainGoal);
        _chain.Foreground = PixelAssets.Current.Brush(chain > 0 ? "gold" : "mist");
    }

    /// <summary>Раз в секунду обновляет время до смены заданий.</summary>
    public void Tick(DateTimeOffset now)
    {
        foreach (var row in _rows.Values)
        {
            row.ShowRemaining(now);
        }
    }

    public void Celebrate(QuestTier tier) => PixelAnimations.Flash(_rows[tier]);
}

/// <summary>Задание одной сложности: значок, текст, полоса прогресса, награда и таймер.</summary>
internal sealed class QuestRow : PixelFrame
{
    private readonly TextCatalog _texts;
    private readonly PixelImage _icon = new() { Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly PixelText _remaining = new() { Foreground = PixelAssets.Current.Brush("mist") };
    private readonly PixelText _title = new() { Wrap = true };
    private readonly PixelMeter _meter = new() { Margin = new Thickness(0, 3, 0, 2) };
    private readonly PixelText _progress = new();
    private readonly PixelText _reward = new() { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly PixelButton _open;
    private readonly StackPanel _details;
    private QuestView? _quest;

    public QuestRow(TextCatalog texts, QuestTier tier)
    {
        _texts = texts;
        Sprite = "inset";
        Padding = new Thickness(4);
        Margin = new Thickness(0, 0, 0, 2);
        AutomationProperties.SetAutomationId(this, "quests.row:" + tier.ToString().ToLowerInvariant());

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(_remaining, Dock.Right);
        header.Children.Add(_remaining);
        header.Children.Add(new PixelText { Text = texts.Get($"quests.tier.{tier}"), Foreground = PixelAssets.Current.Brush("gold") });

        _open = new PixelButton { Content = texts.Get("quests.open"), Focusable = false, Margin = new Thickness(3, 0, 3, 0), Visibility = Visibility.Collapsed };
        _open.Click += (_, _) => OpenLink(_quest?.Url);
        AutomationProperties.SetAutomationId(_open, "quests.open:" + tier.ToString().ToLowerInvariant());

        var footer = new DockPanel();
        _progress.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_progress, Dock.Left);
        DockPanel.SetDock(_open, Dock.Left);
        footer.Children.Add(_progress);
        footer.Children.Add(_open);
        footer.Children.Add(_reward);

        _details = new StackPanel();
        _details.Children.Add(_meter);
        _details.Children.Add(footer);

        var text = new StackPanel();
        text.Children.Add(_title);
        text.Children.Add(_details);

        var body = new DockPanel();
        DockPanel.SetDock(_icon, Dock.Left);
        body.Children.Add(_icon);
        body.Children.Add(text);

        Child = new StackPanel { Children = { header, body } };
    }

    internal QuestView? Quest => _quest;

    /// <summary>Все тексты строки, для проверок.</summary>
    internal IReadOnlyList<string> Texts => [_title.Text, _progress.Text, _reward.Text, _remaining.Text];

    internal double Share => _meter.Share;

    /// <summary>Кнопка «Открыть» у заданий со ссылкой, для проверок.</summary>
    internal PixelButton OpenButton => _open;

    /// <summary>Открывает ссылку задания в браузере по умолчанию. Только http и https: другие схемы могли бы запустить программу.</summary>
    internal static bool OpenLink(string? url)
    {
        if (!IsWebLink(url))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool IsWebLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public void Show(QuestView? quest, DateTimeOffset now)
    {
        _quest = quest;
        if (quest is null)
        {
            _icon.Sprite = "clock";
            _title.Text = _texts.Get("quests.none");
            _title.Foreground = PixelAssets.Current.Brush("mist");
            _details.Visibility = Visibility.Collapsed;
            _open.Visibility = Visibility.Collapsed;
            _remaining.Text = string.Empty;
            return;
        }

        _icon.Sprite = quest.Completed ? "check" : quest.Icon;
        _title.Text = quest.Title;
        _title.Foreground = PixelAssets.Current.Brush("text");
        _details.Visibility = Visibility.Visible;
        _meter.Color = quest.Completed ? "green" : "gold";
        _meter.Share = quest.Share;
        _progress.Text = QuestFormat.Progress(quest);
        _open.Visibility = IsWebLink(quest.Url) ? Visibility.Visible : Visibility.Collapsed;
        if (quest.Completed)
        {
            _reward.Text = _texts.Get("quests.done");
            _reward.Foreground = PixelAssets.Current.Brush("green");
        }
        else
        {
            _reward.Text = _texts.Get("quests.reward", CoinFormat.Short(quest.Reward));
            _reward.Foreground = PixelAssets.Current.Brush("gold");
        }

        ShowRemaining(now);
    }

    public void ShowRemaining(DateTimeOffset now) =>
        _remaining.Text = _quest is null ? string.Empty : _texts.Get("quests.refresh", QuestFormat.Remaining(_quest.Refresh - now));
}
