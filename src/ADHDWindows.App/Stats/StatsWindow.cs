using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Stats;

namespace ADHDWindows.App.Stats;

/// <summary>
/// Окно «Логи»: вкладки Сводка, Доходы, Коллекция, Задания и Лента. Содержимое вкладок перестраивается
/// при каждом обновлении, а сами вкладки и прокрутка остаются на месте.
/// </summary>
internal sealed class StatsWindow : PixelWindow
{
    public const int DayCount = 14;
    public const int WeekCount = 8;

    private const int ContentWidth = 300;
    private const int TabHeight = 230;

    /// <summary>Ширина графика: делится и на 14 дней, и на 8 недель, и помещается рядом с полосой прокрутки.</summary>
    private const int ChartWidth = 280;

    private const int ChartHeight = 48;
    private const string QuestPrefix = "quests.";
    private static readonly string[] QuestDifficulties = ["easy", "medium", "hard"];

    private readonly TextCatalog _texts;
    private readonly TabControl _tabs = new() { Height = TabHeight };
    private readonly Dictionary<string, StackPanel> _pages = new(StringComparer.Ordinal);
    private readonly PixelText _since;
    private readonly PixelText _empty;
    private StatsSnapshot? _snapshot;
    private bool _weeks;

    public StatsWindow(TextCatalog texts, Action onExport, Action onReset)
    {
        _texts = texts;
        Title = texts.Get("stats.title");
        IconSprite = "chart";

        var layout = new StackPanel { Width = ContentWidth };
        _empty = new PixelText { Text = texts.Get("stats.empty"), Wrap = true, Visibility = Visibility.Collapsed };
        layout.Children.Add(_empty);

        foreach (var page in new[] { "summary", "income", "collection", "quests", "feed" })
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
            _pages[page] = panel;
            var tab = new TabItem
            {
                Header = texts.Get($"stats.tab.{page}"),
                Tag = page,
                Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false },
            };
            AutomationProperties.SetAutomationId(tab, "stats.tab:" + page);
            _tabs.Items.Add(tab);
        }

        _tabs.SelectedIndex = 0;
        layout.Children.Add(_tabs);

        var footer = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        Reset = new PixelButton { Content = texts.Get("stats.reset"), Kind = PixelButtonKind.Danger, Focusable = false };
        Reset.Click += (_, _) => onReset();
        DockPanel.SetDock(Reset, Dock.Right);
        Export = new PixelButton { Content = texts.Get("stats.export"), Margin = new Thickness(0, 0, 3, 0), Focusable = false };
        Export.Click += (_, _) => onExport();
        DockPanel.SetDock(Export, Dock.Right);
        AutomationProperties.SetAutomationId(Reset, "stats.reset");
        AutomationProperties.SetAutomationId(Export, "stats.export");
        _since = Dim(string.Empty);
        _since.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(Reset);
        footer.Children.Add(Export);
        footer.Children.Add(_since);
        layout.Children.Add(footer);

        TextElement.SetForeground(layout, PixelAssets.Current.Brush("text"));
        Content = layout;
    }

    public PixelButton Export { get; }

    public PixelButton Reset { get; }

    internal int TabCount => _tabs.Items.Count;

    internal StatsSnapshot? Snapshot => _snapshot;

    /// <summary>Значения графика доходов, для проверок.</summary>
    internal IReadOnlyList<long> ChartValues { get; private set; } = [];

    internal void SelectTab(int index) => _tabs.SelectedIndex = index;

    internal void ShowWeeks(bool weeks)
    {
        _weeks = weeks;
        if (_snapshot is not null)
        {
            BuildIncome(_snapshot);
        }
    }

    /// <summary>Все тексты вкладки, для проверок.</summary>
    internal IReadOnlyList<string> TextsOf(string page) =>
        [.. Descendants(_pages[page]).OfType<PixelText>().Select(text => text.Text)];

    public void ShowUnavailable()
    {
        _empty.Visibility = Visibility.Visible;
        _tabs.Visibility = Visibility.Collapsed;
        Export.IsEnabled = false;
        Reset.IsEnabled = false;
    }

    public void Update(StatsSnapshot snapshot)
    {
        _snapshot = snapshot;
        _empty.Visibility = Visibility.Collapsed;
        _tabs.Visibility = Visibility.Visible;
        Export.IsEnabled = true;
        Reset.IsEnabled = true;
        _since.Text = _texts.Get("stats.since", StatsFormat.Date(snapshot.Since));
        BuildSummary(snapshot);
        BuildIncome(snapshot);
        BuildCollection(snapshot);
        BuildQuests(snapshot);
        BuildFeed(snapshot);
    }

    private void BuildSummary(StatsSnapshot snapshot)
    {
        var page = Clear("summary");
        Section(page, "stats.section.coins");
        Row(page, "coin", "stats.balance", CoinFormat.Short(snapshot.Balance));
        Row(page, "arrow_up", "stats.earned", CoinFormat.Short(snapshot.TotalEarned));
        Row(page, "shop", "stats.spent", CoinFormat.Short(snapshot.TotalSpent));
        Row(page, "trophy", "stats.bestDay", snapshot.BestDay is { } best
            ? _texts.Get("stats.bestDay.value", CoinFormat.Short(best.Earned), best.Date.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.CurrentCulture))
            : "0");

        Section(page, "stats.section.activity");
        foreach (var button in new[] { "left", "right", "middle" })
        {
            Row(page, "cursor", $"stats.clicks.{button}", StatsFormat.Number(snapshot.Clicks.GetValueOrDefault(button)));
        }

        Row(page, "star", "stats.clicksPerSecond", StatsFormat.Number(snapshot.BestClicksPerSecond));
        Row(page, "cursor", "stats.cursor", _texts.Get("stats.cursor.value", StatsFormat.Meters(snapshot.CursorMeters)));
        Row(page, "clock", "stats.appTime", StatsFormat.Duration(_texts, snapshot.AppSeconds));

        Section(page, "stats.section.time");
        foreach (var source in new[] { "video", "music", "game" })
        {
            Row(page, "clock", $"stats.time.{source}", StatsFormat.Duration(_texts, snapshot.MediaSeconds.GetValueOrDefault(source)));
        }

        Section(page, "stats.section.favorites");
        TopList(page, "stats.top.sites", snapshot.TopSites);
        TopList(page, "stats.top.programs", snapshot.TopPrograms);
        TopList(page, "stats.top.games", snapshot.TopGames);

        Section(page, "stats.section.progress");
        var counters = snapshot.Counters.Where(pair => !pair.Key.StartsWith(QuestPrefix, StringComparison.Ordinal)).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        if (counters.Count == 0)
        {
            page.Children.Add(Dim(_texts.Get("stats.progress.none"), wrap: true));
        }

        foreach (var (key, value) in counters)
        {
            ValueRow(page, "star", StatsFormat.Label(_texts, $"stats.counter.{key}", key), StatsFormat.Number(value));
        }
    }

    private void BuildIncome(StatsSnapshot snapshot)
    {
        var page = Clear("income");
        var toggle = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 4) };
        var days = new PixelButton { Content = _texts.Get("stats.chart.days"), Kind = _weeks ? PixelButtonKind.Normal : PixelButtonKind.Primary, Focusable = false };
        var weeks = new PixelButton { Content = _texts.Get("stats.chart.weeks"), Kind = _weeks ? PixelButtonKind.Primary : PixelButtonKind.Normal, Margin = new Thickness(3, 0, 0, 0), Focusable = false };
        days.Click += (_, _) => ShowWeeks(false);
        weeks.Click += (_, _) => ShowWeeks(true);
        toggle.Children.Add(days);
        toggle.Children.Add(weeks);
        page.Children.Add(toggle);

        var today = DateOnly.FromDateTime(snapshot.Taken.ToLocalTime().DateTime);
        var points = _weeks
            ? StatsSeries.Weeks(snapshot.Days, today, WeekCount, day => day.Earned)
            : StatsSeries.Days(snapshot.Days, today, DayCount, day => day.Earned);
        ChartValues = [.. points.Select(point => point.Value)];

        Section(page, _weeks ? "stats.chart.title.weeks" : "stats.chart.title.days");
        var max = ChartValues.Count == 0 ? 0 : ChartValues.Max();
        page.Children.Add(Dim(_texts.Get("stats.chart.max", CoinFormat.Short(max))));
        var chart = new PixelBarChart
        {
            Values = ChartValues,
            Width = ChartWidth,
            Height = ChartHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 1),
        };
        AutomationProperties.SetAutomationId(chart, "stats.chart");
        page.Children.Add(chart);

        var labels = new UniformGrid { Columns = points.Count, Width = ChartWidth, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var point in points)
        {
            var label = _weeks ? point.Start.ToString("dd.MM", System.Globalization.CultureInfo.CurrentCulture) : point.Start.Day.ToString(System.Globalization.CultureInfo.CurrentCulture);
            labels.Children.Add(new PixelText { Text = label, TextAlignment = TextAlignment.Center, Foreground = PixelAssets.Current.Brush("mist") });
        }

        page.Children.Add(labels);

        Section(page, "stats.sources");
        var total = snapshot.EarnedBySource.Values.Sum();
        if (total == 0)
        {
            page.Children.Add(Dim(_texts.Get("stats.sources.none")));
            return;
        }

        foreach (var (source, amount) in snapshot.EarnedBySource.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var share = (double)amount / total;
            ValueRow(page, "coin", StatsFormat.Label(_texts, $"stats.source.{source}", source), $"{CoinFormat.Short(amount)} — {StatsFormat.Percent(share)}%");
            page.Children.Add(new PixelMeter { Share = share, Margin = new Thickness(0, 1, 0, 3) });
        }
    }

    private void BuildCollection(StatsSnapshot snapshot)
    {
        var page = Clear("collection");
        Section(page, "stats.collection.title");
        page.Children.Add(new PixelText { Text = _texts.Get("stats.collection.share", StatsFormat.Percent(snapshot.CatalogShare)) });
        page.Children.Add(new PixelMeter { Share = snapshot.CatalogShare, Color = "green", Margin = new Thickness(0, 2, 0, 3) });
        foreach (var part in snapshot.Catalog.OrderBy(part => part.Kind))
        {
            page.Children.Add(Dim(_texts.Get($"stats.collection.kind.{part.Kind}", part.Unlocked, part.Total)));
        }

        var entries = snapshot.Collection
            .Select(item => (item.When, Icon: IconOf(item.Kind), Name: item.Name, item.Price))
            .Concat(snapshot.Upgrades.Select(upgrade => (upgrade.When, Icon: "arrow_up", Name: _texts.Get("stats.collection.upgrade", upgrade.Name, upgrade.Level), upgrade.Price)))
            .OrderByDescending(entry => entry.When)
            .ToList();

        Section(page, "stats.collection.list");
        if (entries.Count == 0)
        {
            page.Children.Add(Dim(_texts.Get("stats.collection.none")));
        }

        foreach (var entry in entries)
        {
            page.Children.Add(Entry(entry.Icon, entry.Name, _texts.Get("stats.collection.item", StatsFormat.Date(entry.When), CoinFormat.Short(entry.Price))));
        }
    }

    private void BuildQuests(StatsSnapshot snapshot)
    {
        var page = Clear("quests");
        Section(page, "stats.quests.title");
        var keys = QuestDifficulties.Select(level => $"{QuestPrefix}completed.{level}")
            .Concat(snapshot.Counters.Keys.Where(key => key.StartsWith(QuestPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            .Distinct()
            .ToList();
        foreach (var key in keys)
        {
            ValueRow(page, "quest", StatsFormat.Label(_texts, $"stats.counter.{key}", key), StatsFormat.Number(snapshot.Counters.GetValueOrDefault(key)));
        }

        if (!snapshot.Counters.Keys.Any(key => key.StartsWith(QuestPrefix, StringComparison.Ordinal)))
        {
            page.Children.Add(Dim(_texts.Get("stats.quests.none"), wrap: true));
        }
    }

    private void BuildFeed(StatsSnapshot snapshot)
    {
        var page = Clear("feed");
        if (snapshot.Feed.Count == 0)
        {
            page.Children.Add(Dim(_texts.Get("stats.feed.none")));
        }

        foreach (var entry in snapshot.Feed)
        {
            page.Children.Add(Entry(entry.Icon, entry.Text, StatsFormat.Moment(entry.When)));
        }
    }

    private static string IconOf(LockKind kind) => kind switch
    {
        LockKind.Game => "trophy",
        LockKind.Site => "unlock",
        _ => "gear",
    };

    private StackPanel Clear(string page)
    {
        var panel = _pages[page];
        panel.Children.Clear();
        return panel;
    }

    private void Section(StackPanel page, string key) => page.Children.Add(new PixelText
    {
        Text = _texts.Get(key),
        FontRole = PixelFontRole.Title,
        Foreground = PixelAssets.Current.Brush("gold"),
        Margin = new Thickness(0, page.Children.Count == 0 ? 0 : 6, 0, 3),
    });

    private void Row(StackPanel page, string icon, string label, string value) => ValueRow(page, icon, _texts.Get(label), value);

    /// <summary>Строка «значок, подпись слева, значение справа».</summary>
    private static void ValueRow(StackPanel page, string icon, string label, string value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 1) };
        var image = new PixelImage { Sprite = icon, Margin = new Thickness(0, 0, 3, 0) };
        DockPanel.SetDock(image, Dock.Left);
        var number = new PixelText { Text = value, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        DockPanel.SetDock(number, Dock.Right);
        row.Children.Add(image);
        row.Children.Add(number);
        row.Children.Add(new PixelText { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = PixelAssets.Current.Brush("mist") });
        page.Children.Add(row);
    }

    private void TopList(StackPanel page, string key, IReadOnlyList<UsageEntry> entries)
    {
        page.Children.Add(new PixelText { Text = _texts.Get(key), Margin = new Thickness(0, 2, 0, 1) });
        if (entries.Count == 0)
        {
            page.Children.Add(Dim(_texts.Get("stats.top.none")));
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 1) };
            var time = new PixelText { Text = StatsFormat.Duration(_texts, entry.Seconds), Margin = new Thickness(4, 0, 0, 0) };
            DockPanel.SetDock(time, Dock.Right);
            row.Children.Add(time);
            row.Children.Add(new PixelText { Text = $"{index + 1}. {entry.Name}", Foreground = PixelAssets.Current.Brush("mist") });
            page.Children.Add(row);
        }
    }

    /// <summary>Запись списка: значок, текст и подпись под ним.</summary>
    private static PixelFrame Entry(string icon, string text, string caption)
    {
        var image = new PixelImage { Sprite = icon, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(image, Dock.Left);
        var lines = new StackPanel();
        lines.Children.Add(new PixelText { Text = text, Wrap = true });
        lines.Children.Add(new PixelText { Text = caption, Foreground = PixelAssets.Current.Brush("mist"), Margin = new Thickness(0, 1, 0, 0) });
        var row = new DockPanel();
        row.Children.Add(image);
        row.Children.Add(lines);
        return new PixelFrame { Sprite = "inset", Padding = new Thickness(3), Margin = new Thickness(0, 0, 0, 2), Child = row };
    }

    private static PixelText Dim(string text, bool wrap = false) => new()
    {
        Text = text,
        Wrap = wrap,
        Foreground = PixelAssets.Current.Brush("mist"),
    };

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
