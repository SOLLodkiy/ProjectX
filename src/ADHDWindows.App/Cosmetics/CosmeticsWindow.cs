using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Cosmetics;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;

namespace ADHDWindows.App.Cosmetics;

/// <summary>
/// Окно «Косметика»: вкладки курсоров, тем, монеток, эффектов, питомца и звука. Строки обновляются на месте,
/// поэтому покупка не сбрасывает вкладку и прокрутку.
/// </summary>
internal sealed class CosmeticsWindow : PixelWindow
{
    private const int ContentWidth = 300;
    private const int RowsHeight = 210;

    private static readonly (string Tab, CosmeticSlot[] Slots)[] Tabs =
    [
        ("cursor", [CosmeticSlot.Cursor]),
        ("theme", [CosmeticSlot.Theme]),
        ("coin", [CosmeticSlot.Coin]),
        ("effects", [CosmeticSlot.Trail, CosmeticSlot.Click, CosmeticSlot.Purchase]),
        ("pet", [CosmeticSlot.Pet]),
    ];

    private readonly TextCatalog _texts;
    private readonly IEventBus _events;
    private readonly PixelText _balance = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TabControl _tabs = new() { Height = RowsHeight + 20 };
    private readonly Dictionary<string, CosmeticRow> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<CosmeticSlot, CosmeticRow> _standard = [];
    private readonly PixelButton _effectsToggle;
    private readonly PetPanel _petPanel;
    private readonly SoundPanel _soundPanel;
    private string _structure = string.Empty;
    private CosmeticsChanged? _state;

    public CosmeticsWindow(TextCatalog texts, IEventBus events)
    {
        _texts = texts;
        _events = events;
        Title = texts.Get("cosmetics.title");
        IconSprite = "star";

        var layout = new StackPanel { Width = ContentWidth };
        var balanceRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        balanceRow.Children.Add(new PixelImage { Sprite = "coin", Margin = new Thickness(0, 0, 4, 0) });
        balanceRow.Children.Add(_balance);
        layout.Children.Add(balanceRow);
        layout.Children.Add(_tabs);

        _effectsToggle = Toggle("cosmetics.effects", () => events.Publish(new CosmeticEffectsRequested(!(_state?.EffectsEnabled ?? true))));
        _petPanel = new PetPanel(texts, events);
        _soundPanel = new SoundPanel(texts, events);

        TextElement.SetForeground(layout, PixelAssets.Current.Brush("text"));
        Content = layout;
    }

    /// <summary>Строки предметов по кодам, для проверок.</summary>
    internal IReadOnlyDictionary<string, CosmeticRow> Rows => _rows;

    internal PetPanel Pet => _petPanel;

    internal SoundPanel Sound => _soundPanel;

    internal int TabCount => _tabs.Items.Count;

    internal void SelectTab(string id) =>
        _tabs.SelectedItem = _tabs.Items.OfType<TabItem>().FirstOrDefault(tab => (string)tab.Tag == id);

    public void Update(CosmeticsChanged state, long balance)
    {
        _state = state;
        var structure = string.Join('|', state.Items.Select(offer => offer.Item.Code));
        if (structure != _structure)
        {
            Rebuild(state);
            _structure = structure;
        }

        foreach (var offer in state.Items)
        {
            _rows[offer.Item.Code].Show(offer, balance);
        }

        foreach (var (slot, row) in _standard)
        {
            row.ShowStandard(!state.Equipped.ContainsKey(slot));
        }

        _effectsToggle.Content = _texts.Get(state.EffectsEnabled ? "cosmetics.on" : "cosmetics.off");
        ShowBalance(balance);
    }

    public void ShowBalance(long balance)
    {
        _balance.Text = _texts.Get("shop.balance", CoinFormat.Short(balance));
        foreach (var row in _rows.Values)
        {
            row.ShowBalance(balance);
        }

        _petPanel.ShowBalance(balance);
    }

    public void ShowPet(PetState state) => _petPanel.Show(state);

    public void ShowSound(SoundSettings settings) => _soundPanel.Show(settings);

    public void Celebrate(string code)
    {
        if (_rows.TryGetValue(code, out var row))
        {
            PixelAnimations.Flash(row);
        }
    }

    public void Refuse(string code)
    {
        if (_rows.TryGetValue(code, out var row))
        {
            PixelAnimations.Shake(row);
        }
    }

    public void RefuseFeed() => PixelAnimations.Shake(_petPanel);

    private void Rebuild(CosmeticsChanged state)
    {
        var selected = (_tabs.SelectedItem as TabItem)?.Tag as string;
        _tabs.Items.Clear();
        _rows.Clear();
        _standard.Clear();
        foreach (var (tab, slots) in Tabs)
        {
            var list = new StackPanel();
            if (tab == "effects")
            {
                list.Children.Add(Line(_texts.Get("cosmetics.effects.enabled"), _effectsToggle));
            }

            if (tab == "pet")
            {
                Detach(_petPanel);
                list.Children.Add(_petPanel);
            }

            foreach (var slot in slots)
            {
                if (slots.Length > 1)
                {
                    list.Children.Add(new PixelText
                    {
                        Text = _texts.Get($"cosmetics.slot.{slot.ToString().ToLowerInvariant()}"),
                        Foreground = PixelAssets.Current.Brush("gold"),
                        Margin = new Thickness(0, 4, 0, 2),
                    });
                }

                // У питомца нет «стандартного вида»: его выключают в настройках выше.
                if (slot != CosmeticSlot.Pet)
                {
                    var standard = CosmeticRow.Standard(_texts, slot, () => _events.Publish(new CosmeticEquipRequested(slot, null)));
                    _standard[slot] = standard;
                    list.Children.Add(standard);
                }

                foreach (var offer in state.Items.Where(offer => offer.Item.Slot == slot))
                {
                    var row = new CosmeticRow(_texts, offer.Item, _events);
                    _rows[offer.Item.Code] = row;
                    list.Children.Add(row);
                }
            }

            AddTab(tab, list);
        }

        Detach(_soundPanel);
        AddTab("sound", _soundPanel);
        _tabs.SelectedItem = _tabs.Items.OfType<TabItem>().FirstOrDefault(tab => (string)tab.Tag == selected)
            ?? _tabs.Items.OfType<TabItem>().FirstOrDefault();
    }

    private void AddTab(string id, UIElement content)
    {
        var tab = new TabItem
        {
            Header = _texts.Get($"cosmetics.tab.{id}"),
            Tag = id,
            Content = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Focusable = false,
            },
        };
        AutomationProperties.SetAutomationId(tab, "cosmetics.tab:" + id);
        _tabs.Items.Add(tab);
    }

    private static void Detach(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case Panel panel:
                panel.Children.Remove(element);
                break;
            case ContentControl control:
                control.Content = null;
                break;
        }
    }

    /// <summary>Строка «подпись — кнопка справа».</summary>
    internal static DockPanel Line(string text, UIElement control)
    {
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        DockPanel.SetDock(control, Dock.Right);
        line.Children.Add(control);
        line.Children.Add(new PixelText { Text = text, VerticalAlignment = VerticalAlignment.Center, Wrap = true });
        return line;
    }

    internal static PixelButton Toggle(string id, Action click)
    {
        var button = new PixelButton { Focusable = false, MinWidth = 40, Margin = new Thickness(4, 0, 0, 0) };
        AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => click();
        return button;
    }
}

/// <summary>Строка предмета: картинка, название с редкостью, описание и кнопка «Купить», «Надеть» или «Снять».</summary>
internal sealed class CosmeticRow : PixelFrame
{
    private readonly TextCatalog _texts;
    private readonly CosmeticItem? _item;
    private readonly PixelText _status;
    private CosmeticOffer? _offer;
    private Action? _action;

    public CosmeticRow(TextCatalog texts, CosmeticItem item, IEventBus events)
        : this(texts, item, CosmeticsScreen.SpritePrefix + item.Code, texts.Get($"cosmetics.{item.Code}.title"), texts.Get($"cosmetics.{item.Code}.text"))
    {
        Button.Click += (_, _) =>
        {
            if (_offer is not { } offer)
            {
                return;
            }

            if (!offer.Owned)
            {
                events.Publish(new CosmeticPurchaseRequested(item.Code));
            }
            else
            {
                events.Publish(new CosmeticEquipRequested(item.Slot, offer.Equipped && item.Slot != CosmeticSlot.Pet ? null : item.Code));
            }
        };
        AutomationProperties.SetAutomationId(Button, "cosmetics.item:" + item.Code);
    }

    private CosmeticRow(TextCatalog texts, CosmeticItem? item, string? sprite, string title, string description)
    {
        _texts = texts;
        _item = item;
        Sprite = "inset";
        Padding = new Thickness(4);
        Margin = new Thickness(0, 0, 0, 2);

        Button = new PixelButton
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Focusable = false,
            MinWidth = 44,
        };
        Button.Click += (_, _) => _action?.Invoke();
        DockPanel.SetDock(Button, Dock.Right);

        var icon = new PixelImage { Sprite = sprite, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(icon, Dock.Left);

        var titleRow = new DockPanel();
        if (item is not null)
        {
            var rarity = new PixelText
            {
                Text = texts.Get($"cosmetics.rarity.{item.Rarity.ToString().ToLowerInvariant()}"),
                Foreground = PixelAssets.Current.Brush(RarityColor(item.Rarity)),
                Margin = new Thickness(4, 0, 0, 0),
            };
            DockPanel.SetDock(rarity, Dock.Right);
            titleRow.Children.Add(rarity);
        }

        titleRow.Children.Add(new PixelText { Text = title, Wrap = true });
        _status = new PixelText { Wrap = true, Margin = new Thickness(0, 2, 0, 0) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(titleRow);
        text.Children.Add(new PixelText
        {
            Text = description,
            Wrap = true,
            Foreground = PixelAssets.Current.Brush("mist"),
            Margin = new Thickness(0, 2, 0, 0),
        });
        text.Children.Add(_status);

        var row = new DockPanel();
        row.Children.Add(Button);
        row.Children.Add(icon);
        row.Children.Add(text);
        Child = row;
    }

    public PixelButton Button { get; }

    internal string Status => _status.Text;

    /// <summary>Строка «Стандартный вид»: снимает предмет с места.</summary>
    public static CosmeticRow Standard(TextCatalog texts, CosmeticSlot slot, Action remove)
    {
        var row = new CosmeticRow(texts, null, null, texts.Get("cosmetics.standard"), texts.Get("cosmetics.standard.text"))
        {
            _action = remove,
        };
        AutomationProperties.SetAutomationId(row.Button, "cosmetics.standard:" + slot);
        return row;
    }

    public void ShowStandard(bool active)
    {
        Button.Content = _texts.Get("cosmetics.equip");
        Button.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        _status.Text = active ? _texts.Get("cosmetics.equipped") : string.Empty;
        _status.Foreground = PixelAssets.Current.Brush("green");
    }

    public void Show(CosmeticOffer offer, long balance)
    {
        _offer = offer;
        var item = offer.Item;
        Button.Visibility = Visibility.Visible;
        Button.Kind = PixelButtonKind.Normal;
        if (offer.Owned)
        {
            var removable = item.Slot != CosmeticSlot.Pet;
            Button.Content = _texts.Get(offer.Equipped ? "cosmetics.remove" : "cosmetics.equip");
            Button.Visibility = offer.Equipped && !removable ? Visibility.Collapsed : Visibility.Visible;
            _status.Text = _texts.Get(offer.Equipped ? "cosmetics.equipped" : "cosmetics.owned");
            _status.Foreground = PixelAssets.Current.Brush(offer.Equipped ? "green" : "mist");
        }
        else if (item.ForSale)
        {
            Button.Content = _texts.Get("cosmetics.buy");
            Button.Kind = PixelButtonKind.Primary;
            _status.Text = _texts.Get("cosmetics.price", CoinFormat.Short(item.Price));
            _status.Foreground = PixelAssets.Current.Brush("gold");
        }
        else
        {
            Button.Visibility = Visibility.Collapsed;
            _status.Text = item.UnlockLevel > 0 ? _texts.Get("cosmetics.fromLevel", item.UnlockLevel)
                : item.Chest ? _texts.Get("cosmetics.fromChest")
                : _texts.Get("cosmetics.fromSeason");
            _status.Foreground = PixelAssets.Current.Brush("mist");
        }

        ShowBalance(balance);
    }

    public void ShowBalance(long balance)
    {
        if (_offer is { Owned: false, Item.ForSale: true } offer)
        {
            Button.IsEnabled = balance >= offer.Item.Price;
        }
        else
        {
            Button.IsEnabled = true;
        }
    }

    private static string RarityColor(CosmeticRarity rarity) => rarity switch
    {
        CosmeticRarity.Rare => "blue",
        CosmeticRarity.Epic => "violet",
        CosmeticRarity.Legendary => "gold",
        _ => "mist",
    };
}

/// <summary>Питомец: картинка, уровень и сытость, «Покормить», показывать ли его и где он живёт.</summary>
internal sealed class PetPanel : PixelFrame
{
    private readonly TextCatalog _texts;
    private readonly PixelImage _image = new() { Zoom = 2, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly PixelText _level = new();
    private readonly PixelText _food = new() { Wrap = true, Margin = new Thickness(0, 2, 0, 0) };
    private readonly PixelButton _show;
    private readonly PixelButton _place;
    private PetState? _state;

    public PetPanel(TextCatalog texts, IEventBus events)
    {
        _texts = texts;
        Sprite = "inset";
        Padding = new Thickness(4);
        Margin = new Thickness(0, 0, 0, 4);

        Feed = new PixelButton { Kind = PixelButtonKind.Primary, Focusable = false, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(Feed, "pet.feed");
        Feed.Click += (_, _) => events.Publish(new PetFeedRequested());
        _show = CosmeticsWindow.Toggle("pet.show", () =>
        {
            if (_state is { } state)
            {
                events.Publish(new PetSettingsRequested(!state.Enabled, state.Place));
            }
        });
        _place = CosmeticsWindow.Toggle("pet.place", () =>
        {
            if (_state is { } state)
            {
                events.Publish(new PetSettingsRequested(state.Enabled, state.Place == PetPlace.Hud ? PetPlace.Taskbar : PetPlace.Hud));
            }
        });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(_level);
        info.Children.Add(_food);
        info.Children.Add(Feed);
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(_image, Dock.Left);
        top.Children.Add(_image);
        top.Children.Add(info);

        var layout = new StackPanel();
        layout.Children.Add(top);
        layout.Children.Add(CosmeticsWindow.Line(texts.Get("pet.show"), _show));
        layout.Children.Add(CosmeticsWindow.Line(texts.Get("pet.place"), _place));
        Child = layout;
    }

    public PixelButton Feed { get; }

    internal PixelButton ShowButton => _show;

    internal string Level => _level.Text;

    public void Show(PetState state)
    {
        _state = state;
        _image.Sprite = state.Species is null ? null : CosmeticsScreen.SpritePrefix + state.Species;
        _level.Text = state.Species is null
            ? _texts.Get("pet.none")
            : $"{_texts.Get($"cosmetics.{state.Species}.title")}. {_texts.Get("pet.level", state.Level, state.MaxLevel)}";
        _food.Text = state.Species is null ? string.Empty
            : state.NextLevelFood is { } next ? _texts.Get("pet.food", state.Food, next - state.Food)
            : _texts.Get("pet.food.max");
        _food.Foreground = PixelAssets.Current.Brush("mist");
        Feed.Content = _texts.Get("pet.feed", CoinFormat.Short(state.FeedPrice));
        Feed.Visibility = state.Species is null ? Visibility.Collapsed : Visibility.Visible;
        _show.Content = _texts.Get(state.Enabled ? "cosmetics.on" : "cosmetics.off");
        _place.Content = _texts.Get(state.Place == PetPlace.Hud ? "pet.place.hud" : "pet.place.taskbar");
    }

    public void ShowBalance(long balance) => Feed.IsEnabled = _state is { Enabled: true, Species: not null } state && balance >= state.FeedPrice;
}

/// <summary>Звук: включение звуков и мелодии и их громкость.</summary>
internal sealed class SoundPanel : StackPanel
{
    private const int Step = 10;

    private readonly TextCatalog _texts;
    private readonly PixelButton _effects;
    private readonly PixelButton _music;
    private readonly PixelText _volume = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly PixelText _musicVolume = new() { VerticalAlignment = VerticalAlignment.Center };
    private SoundSettings _settings = SoundSettings.Default;

    public SoundPanel(TextCatalog texts, IEventBus events)
    {
        _texts = texts;
        void Change(Func<SoundSettings, SoundSettings> change) => events.Publish(new SoundSettingsChangeRequested(change(_settings)));

        _effects = CosmeticsWindow.Toggle("sound.effects", () => Change(settings => settings with { Effects = !settings.Effects }));
        _music = CosmeticsWindow.Toggle("sound.music", () => Change(settings => settings with { Music = !settings.Music }));
        Children.Add(CosmeticsWindow.Line(texts.Get("sound.effects"), _effects));
        Children.Add(Volume(_volume, "sound.volume", delta => Change(settings => settings with { Volume = settings.Volume + delta })));
        Children.Add(CosmeticsWindow.Line(texts.Get("sound.music"), _music));
        Children.Add(Volume(_musicVolume, "sound.musicVolume", delta => Change(settings => settings with { MusicVolume = settings.MusicVolume + delta })));
        Children.Add(new PixelText
        {
            Text = texts.Get("sound.quiet"),
            Wrap = true,
            Foreground = PixelAssets.Current.Brush("mist"),
            Margin = new Thickness(0, 4, 0, 0),
        });
    }

    internal PixelButton EffectsButton => _effects;

    internal PixelButton MusicButton => _music;

    public void Show(SoundSettings settings)
    {
        _settings = settings;
        _effects.Content = _texts.Get(settings.Effects ? "cosmetics.on" : "cosmetics.off");
        _music.Content = _texts.Get(settings.Music ? "cosmetics.on" : "cosmetics.off");
        _volume.Text = _texts.Get("sound.volume", settings.Volume);
        _musicVolume.Text = _texts.Get("sound.musicVolume", settings.MusicVolume);
    }

    private static DockPanel Volume(PixelText label, string id, Action<int> change)
    {
        var minus = CosmeticsWindow.Toggle(id + ".down", () => change(-Step));
        minus.Content = "-";
        var plus = CosmeticsWindow.Toggle(id + ".up", () => change(Step));
        plus.Content = "+";
        minus.MinWidth = plus.MinWidth = 18;
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        DockPanel.SetDock(plus, Dock.Right);
        DockPanel.SetDock(minus, Dock.Right);
        line.Children.Add(plus);
        line.Children.Add(minus);
        line.Children.Add(label);
        return line;
    }
}
