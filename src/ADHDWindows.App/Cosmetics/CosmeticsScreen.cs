using System.Windows.Threading;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Cosmetics;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;

namespace ADHDWindows.App.Cosmetics;

/// <summary>
/// Связывает модули косметики, питомца и звука с окном «Косметика» и применяет вид, который рисует само приложение:
/// тему интерфейса, скин монеток и картинки предметов. Создаётся до запуска ядра, чтобы не пропустить первое состояние.
/// Без модуля косметики состояния нет и окно не открывается.
/// </summary>
internal sealed class CosmeticsScreen : IDisposable
{
    /// <summary>Спрайт картинки предмета в окне: «cosmetic:» и код.</summary>
    public const string SpritePrefix = "cosmetic:";

    /// <summary>Спрайт монетки, который подменяет скин.</summary>
    public const string CoinSprite = "coin";

    private readonly IEventBus _events;
    private readonly IEconomy _economy;
    private readonly TextCatalog _texts;
    private readonly Func<bool> _designReady;
    private readonly Dispatcher _dispatcher;
    private readonly List<IDisposable> _subscriptions;
    private readonly HashSet<string> _registered = new(StringComparer.Ordinal);
    private CosmeticsChanged? _state;
    private PetState? _pet;
    private SoundSettings? _sound;
    private CosmeticsWindow? _window;
    private bool _disposed;

    public CosmeticsScreen(IEventBus events, IEconomy economy, TextCatalog texts, Func<bool> designReady, Dispatcher dispatcher)
    {
        _events = events;
        _economy = economy;
        _texts = texts;
        _designReady = designReady;
        _dispatcher = dispatcher;
        _subscriptions =
        [
            events.Subscribe<CosmeticsChanged>(state => OnUi(() => Update(state))),
            events.Subscribe<PetChanged>(change => OnUi(() =>
            {
                _pet = change.State;
                _window?.ShowPet(change.State);
            })),
            events.Subscribe<SoundSettingsChanged>(change => OnUi(() =>
            {
                _sound = change.Settings;
                _window?.ShowSound(change.Settings);
            })),
            events.Subscribe<CosmeticPurchased>(purchased => OnUi(() => _window?.Celebrate(purchased.Code))),
            events.Subscribe<CosmeticPurchaseDenied>(denied => OnUi(() => _window?.Refuse(denied.Code))),
            events.Subscribe<PetFeedDenied>(_ => OnUi(() => _window?.RefuseFeed())),
            events.Subscribe<CosmeticGranted>(granted => OnUi(() => Granted(granted))),
            events.Subscribe<PetFed>(fed => OnUi(() =>
            {
                if (fed.Grew && _designReady())
                {
                    ToastManager.Show(ToastKind.Success, _texts.Get("pet.grew.title"), _texts.Get("pet.grew.text", fed.Level), "heart");
                }
            })),
            events.Subscribe<CoinsEarned>(_ => OnUi(RefreshBalance)),
            events.Subscribe<CoinsSpent>(_ => OnUi(RefreshBalance)),
        ];
    }

    /// <summary>Модуль косметики загружен, и окно можно открыть.</summary>
    public bool Available => _state is not null;

    /// <summary>Окно, если оно открыто, для проверок.</summary>
    internal CosmeticsWindow? Window => _window;

    /// <summary>Просит состояние у модулей. Вызывается после запуска ядра.</summary>
    public void Request()
    {
        _events.Publish(new CosmeticsRequested());
        _events.Publish(new PetRequested());
        _events.Publish(new SoundSettingsRequested());
    }

    /// <summary>Применяет тему и скин монеток, когда дизайн-система уже загружена.</summary>
    public void ApplyLook()
    {
        if (_state is { } state)
        {
            Apply(state);
        }
    }

    public void Open()
    {
        if (!_designReady() || _state is null)
        {
            return;
        }

        if (_window is null)
        {
            _window = new CosmeticsWindow(_texts, _events);
            _window.Closed += (_, _) => _window = null;
            _window.Update(_state, _economy.Balance);
            if (_pet is not null)
            {
                _window.ShowPet(_pet);
            }

            if (_sound is not null)
            {
                _window.ShowSound(_sound);
            }

            _window.Show();
        }

        _window.Activate();
    }

    public void Dispose()
    {
        _disposed = true;
        _subscriptions.ForEach(subscription => subscription.Dispose());
        _window?.Close();
    }

    /// <summary>Применяет состояние: картинки предметов, тему и скин монеток. Без дизайн-системы — только запоминает.</summary>
    internal void Update(CosmeticsChanged state)
    {
        _state = state;
        if (!_designReady())
        {
            return;
        }

        Apply(state);
        _window?.Update(state, _economy.Balance);
    }

    private void Apply(CosmeticsChanged state)
    {
        var assets = PixelAssets.Current;
        foreach (var offer in state.Items)
        {
            var item = offer.Item;
            if (!_registered.Add(item.Code))
            {
                continue;
            }

            var sprite = SpritePrefix + item.Code;
            switch (item.Slot)
            {
                case CosmeticSlot.Theme:
                    assets.RegisterSwatch(sprite, item.Colors);
                    break;
                case CosmeticSlot.Coin:
                    assets.RegisterRecolored(sprite, CoinSprite, item.Recolor);
                    break;
                case CosmeticSlot.Pet:
                    assets.Register(sprite, item.Frames("idle"), item.Fps);
                    break;
                default:
                    assets.Register(sprite, item.Frames(CosmeticItem.DefaultAnimation), item.Fps);
                    break;
            }
        }

        var coin = state.Equipped.GetValueOrDefault(CosmeticSlot.Coin);
        assets.SetAlias(CoinSprite, coin is null ? null : SpritePrefix + coin.Code);
        var themed = assets.ApplyTheme(state.Equipped.GetValueOrDefault(CosmeticSlot.Theme)?.Colors);
        if (!themed)
        {
            // Скин монеток мог смениться без темы: окна перерисовываются, чтобы монетка в HUD сразу стала новой.
            foreach (System.Windows.Window window in System.Windows.Application.Current.Windows)
            {
                assets.Retheme(window);
            }
        }
    }

    private void Granted(CosmeticGranted granted)
    {
        // Содержимое сундука показывает своё уведомление.
        if (!_designReady() || granted.Source.StartsWith("chest", StringComparison.Ordinal))
        {
            return;
        }

        ToastManager.Show(
            ToastKind.Reward,
            _texts.Get("cosmetics.granted.title"),
            _texts.Get("cosmetics.granted.text", _texts.Get($"cosmetics.{granted.Item.Code}.title")),
            "star");
    }

    /// <summary>Действие в потоке интерфейса. После закрытия экрана запоздавшие события ничего не меняют.</summary>
    private void OnUi(Action action) => _dispatcher.BeginInvoke(() =>
    {
        if (!_disposed)
        {
            action();
        }
    });

    private void RefreshBalance() => _window?.ShowBalance(_economy.Balance);
}
