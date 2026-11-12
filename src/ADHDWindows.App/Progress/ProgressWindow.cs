using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using ADHDWindows.App.Design;
using ADHDWindows.App.Stats;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Progress;

namespace ADHDWindows.App.Progress;

/// <summary>
/// Окно «Прогресс»: вкладки Уровень (звание, опыт, серия, цели недели), Достижения и День (итог сегодня и прошлого дня).
/// Внизу — выключатели уведомлений и итога дня. Прогресс считается при любых настройках.
/// </summary>
internal sealed class ProgressWindow : PixelWindow
{
    private const int ContentWidth = 280;
    private const int TabHeight = 250;

    private readonly TextCatalog _texts;
    private readonly Action<string, bool> _setSetting;
    private readonly TabControl _tabs = new() { Height = TabHeight };
    private readonly Dictionary<string, StackPanel> _pages = new(StringComparer.Ordinal);
    private ProgressSnapshot? _snapshot;

    public ProgressWindow(TextCatalog texts, Action<string, bool> setSetting)
    {
        _texts = texts;
        _setSetting = setSetting;
        Title = texts.Get("progress.title");
        IconSprite = "crown";

        var layout = new StackPanel { Width = ContentWidth };
        foreach (var page in new[] { "level", "achievements", "day" })
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
            _pages[page] = panel;
            var tab = new TabItem
            {
                Header = texts.Get($"progress.tab.{page}"),
                Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false },
            };
            AutomationProperties.SetAutomationId(tab, "progress.tab:" + page);
            _tabs.Items.Add(tab);
        }

        _tabs.SelectedIndex = 0;
        layout.Children.Add(_tabs);

        var footer = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 0) };
        Notifications = new PixelButton { Focusable = false };
        Notifications.Click += (_, _) => Toggle(ProgressSettings.Notifications, _snapshot?.Notifications);
        DaySummary = new PixelButton { Focusable = false, Margin = new Thickness(3, 0, 0, 0) };
        DaySummary.Click += (_, _) => Toggle(ProgressSettings.DaySummary, _snapshot?.ShowDaySummary);
        AutomationProperties.SetAutomationId(Notifications, "progress.notifications");
        AutomationProperties.SetAutomationId(DaySummary, "progress.daySummary");
        footer.Children.Add(Notifications);
        footer.Children.Add(DaySummary);
        layout.Children.Add(footer);

        TextElement.SetForeground(layout, PixelAssets.Current.Brush("text"));
        Content = layout;
    }

    public PixelButton Notifications { get; }

    public PixelButton DaySummary { get; }

    internal ProgressSnapshot? Snapshot => _snapshot;

    internal int TabCount => _tabs.Items.Count;

    internal void SelectTab(int index) => _tabs.SelectedIndex = index;

    /// <summary>Все тексты вкладки, для проверок.</summary>
    internal IReadOnlyList<string> TextsOf(string page) => ProgressViews.Texts(_pages[page]);

    public void Update(ProgressSnapshot snapshot)
    {
        _snapshot = snapshot;
        BuildLevel(snapshot);
        BuildAchievements(snapshot);
        BuildDay(snapshot);
        Notifications.Content = _texts.Get(snapshot.Notifications ? "progress.notifications.on" : "progress.notifications.off");
        Notifications.Kind = snapshot.Notifications ? PixelButtonKind.Primary : PixelButtonKind.Normal;
        DaySummary.Content = _texts.Get(snapshot.ShowDaySummary ? "progress.daySummary.on" : "progress.daySummary.off");
        DaySummary.Kind = snapshot.ShowDaySummary ? PixelButtonKind.Primary : PixelButtonKind.Normal;
    }

    public void Celebrate() => PixelAnimations.Flash(_tabs);

    private void Toggle(string key, bool? current)
    {
        if (current is { } value)
        {
            _setSetting(key, !value);
        }
    }

    private void BuildLevel(ProgressSnapshot snapshot)
    {
        var page = Clear("level");
        ProgressViews.Section(page, _texts.Get("progress.level", snapshot.Level));
        var rank = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var crown = new PixelImage { Sprite = "crown", Margin = new Thickness(0, 0, 4, 0) };
        DockPanel.SetDock(crown, Dock.Left);
        rank.Children.Add(crown);
        rank.Children.Add(new PixelText { Text = snapshot.RankName, Foreground = PixelAssets.Current.Brush("gold"), VerticalAlignment = VerticalAlignment.Center });
        page.Children.Add(rank);
        page.Children.Add(new PixelMeter { Share = snapshot.LevelShare, Color = "gold", Margin = new Thickness(0, 1, 0, 1) });
        page.Children.Add(ProgressViews.Dim(snapshot.NextLevelXp > 0
            ? _texts.Get("progress.xp", StatsFormat.Number(snapshot.LevelXp), StatsFormat.Number(snapshot.NextLevelXp))
            : _texts.Get("progress.xp.max")));
        ProgressViews.ValueRow(page, "star", _texts.Get("progress.xp.total"), StatsFormat.Number(snapshot.TotalXp));

        ProgressViews.Section(page, _texts.Get("progress.streak.title"));
        ProgressViews.ValueRow(page, "flame", _texts.Get("progress.streak"), _texts.Get("progress.days", snapshot.Streak));
        ProgressViews.ValueRow(page, "trophy", _texts.Get("progress.streak.best"), _texts.Get("progress.days", snapshot.BestStreak));
        ProgressViews.ValueRow(page, "coin", _texts.Get("progress.streak.next"), CoinFormat.Short(snapshot.NextStreakReward));
        page.Children.Add(ProgressViews.Dim(_texts.Get(snapshot.SkipAvailable ? "progress.skip.available" : "progress.skip.used"), wrap: true));

        ProgressViews.Section(page, _texts.Get("progress.weekly.title"));
        page.Children.Add(ProgressViews.Dim(_texts.Get("progress.weekly.ends", snapshot.WeekStart.AddDays(6).ToString("dd.MM", CultureInfo.CurrentCulture))));
        if (snapshot.WeeklyGoals.Count == 0)
        {
            page.Children.Add(ProgressViews.Dim(_texts.Get("progress.weekly.none")));
        }

        foreach (var goal in snapshot.WeeklyGoals)
        {
            page.Children.Add(ProgressViews.Goal(
                goal.Completed ? "check" : goal.Icon,
                goal.Title,
                goal.Share,
                goal.Completed,
                _texts.Get("progress.goal.progress", Amount(goal, goal.Progress), Amount(goal, goal.Goal)),
                goal.Completed ? _texts.Get("progress.done") : _texts.Get("progress.reward", CoinFormat.Short(goal.Reward))));
        }
    }

    private void BuildAchievements(ProgressSnapshot snapshot)
    {
        var page = Clear("achievements");
        page.Children.Add(new PixelText { Text = _texts.Get("progress.achievements.count", snapshot.UnlockedCount, snapshot.Achievements.Count) });
        page.Children.Add(new PixelMeter
        {
            Share = snapshot.Achievements.Count == 0 ? 0 : (double)snapshot.UnlockedCount / snapshot.Achievements.Count,
            Color = "green",
            Margin = new Thickness(0, 2, 0, 4),
        });

        // Полученные сверху, свежие первыми; остальные — по близости к цели.
        var ordered = snapshot.Achievements
            .OrderByDescending(achievement => achievement.IsUnlocked)
            .ThenByDescending(achievement => achievement.Unlocked)
            .ThenBy(achievement => achievement.Hidden)
            .ThenByDescending(achievement => achievement.Share);
        foreach (var achievement in ordered)
        {
            var secret = achievement.Hidden && !achievement.IsUnlocked;
            var caption = achievement.IsUnlocked
                ? _texts.Get("progress.achievement.unlocked", StatsFormat.Date(achievement.Unlocked!.Value))
                : secret ? _texts.Get("progress.achievement.secret") : achievement.Text;
            page.Children.Add(ProgressViews.Goal(
                achievement.IsUnlocked ? achievement.Icon : secret ? "lock" : achievement.Icon,
                secret ? _texts.Get("progress.achievement.hidden") : achievement.Title,
                secret ? 0 : achievement.Share,
                achievement.IsUnlocked,
                caption,
                achievement.IsUnlocked ? _texts.Get("progress.done") : _texts.Get("progress.reward", CoinFormat.Short(achievement.Reward)),
                dim: !achievement.IsUnlocked));
        }
    }

    private void BuildDay(ProgressSnapshot snapshot)
    {
        var page = Clear("day");
        ProgressViews.Section(page, _texts.Get("progress.day.today"));
        ProgressViews.Summary(page, _texts, snapshot.Today);
        ProgressViews.Section(page, _texts.Get("progress.day.last"));
        if (snapshot.LastSummary is { } last)
        {
            page.Children.Add(ProgressViews.Dim(last.Date.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture)));
            ProgressViews.Summary(page, _texts, last);
        }
        else
        {
            page.Children.Add(ProgressViews.Dim(_texts.Get("progress.day.none"), wrap: true));
        }
    }

    /// <summary>Секунды целей показываются часами, остальное — числом.</summary>
    private string Amount(WeeklyGoalView goal, double value) =>
        goal.IsTime ? StatsFormat.Duration(_texts, value) : StatsFormat.Number((long)Math.Floor(value));

    private StackPanel Clear(string page)
    {
        var panel = _pages[page];
        panel.Children.Clear();
        return panel;
    }
}

/// <summary>Общие строки окон прогресса и итога дня.</summary>
internal static class ProgressViews
{
    public static void Section(StackPanel page, string text) => page.Children.Add(new PixelText
    {
        Text = text,
        FontRole = PixelFontRole.Title,
        Foreground = PixelAssets.Current.Brush("gold"),
        Margin = new Thickness(0, page.Children.Count == 0 ? 0 : 6, 0, 3),
    });

    /// <summary>Строка «значок, подпись слева, значение справа».</summary>
    public static void ValueRow(StackPanel page, string icon, string label, string value)
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

    /// <summary>Цель или достижение: значок, название, полоса, подпись и награда.</summary>
    public static PixelFrame Goal(string icon, string title, double share, bool done, string caption, string reward, bool dim = false)
    {
        var image = new PixelImage { Sprite = icon, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(image, Dock.Left);
        var footer = new DockPanel();
        var right = new PixelText { Text = reward, Foreground = PixelAssets.Current.Brush(done ? "green" : "gold"), Margin = new Thickness(4, 0, 0, 0) };
        DockPanel.SetDock(right, Dock.Right);
        footer.Children.Add(right);
        footer.Children.Add(new PixelText { Text = caption, Wrap = true, Foreground = PixelAssets.Current.Brush("mist") });
        var lines = new StackPanel();
        lines.Children.Add(new PixelText { Text = title, Wrap = true, Foreground = PixelAssets.Current.Brush(dim ? "mist" : "text") });
        lines.Children.Add(new PixelMeter { Share = share, Color = done ? "green" : "gold", Margin = new Thickness(0, 2, 0, 1) });
        lines.Children.Add(footer);
        var row = new DockPanel();
        row.Children.Add(image);
        row.Children.Add(lines);
        return new PixelFrame { Sprite = "inset", Padding = new Thickness(3), Margin = new Thickness(0, 0, 0, 2), Child = row };
    }

    /// <summary>Строки итога дня.</summary>
    public static void Summary(StackPanel page, TextCatalog texts, DaySummary day)
    {
        ValueRow(page, "coin", texts.Get("progress.day.earned"), CoinFormat.Short(day.Earned));
        ValueRow(page, "shop", texts.Get("progress.day.spent"), CoinFormat.Short(day.Spent));
        ValueRow(page, "cursor", texts.Get("progress.day.clicks"), StatsFormat.Number(day.Clicks));
        ValueRow(page, "clock", texts.Get("progress.day.media"), StatsFormat.Duration(texts, day.MediaSeconds));
        ValueRow(page, "star", texts.Get("progress.day.xp"), StatsFormat.Number(day.Xp));
        ValueRow(page, "medal", texts.Get("progress.day.achievements"), StatsFormat.Number(day.Achievements));
        ValueRow(page, "crown", texts.Get("progress.day.levels"), StatsFormat.Number(day.Levels));
        ValueRow(page, "flame", texts.Get("progress.streak"), texts.Get("progress.days", day.Streak));
        ValueRow(
            page,
            "trophy",
            texts.Get("progress.day.best"),
            day.BestSource is { } source ? $"{StatsFormat.Label(texts, $"stats.source.{source}", source)}: {CoinFormat.Short(day.BestSourceEarned)}" : texts.Get("hud.none"));
        if (day.Record)
        {
            page.Children.Add(new PixelText { Text = texts.Get("progress.day.record"), Foreground = PixelAssets.Current.Brush("green"), Margin = new Thickness(0, 2, 0, 0) });
        }
    }

    public static PixelText Dim(string text, bool wrap = false) => new()
    {
        Text = text,
        Wrap = wrap,
        Foreground = PixelAssets.Current.Brush("mist"),
    };

    public static IReadOnlyList<string> Texts(DependencyObject root) => [.. Descendants(root).OfType<PixelText>().Select(text => text.Text)];

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
