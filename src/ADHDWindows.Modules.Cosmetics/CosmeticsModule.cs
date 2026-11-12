using System.Collections.Concurrent;
using ADHDWindows.Core.Cosmetics;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Progress;
using ADHDWindows.Core.Upgrades;
using ADHDWindows.Modules.Hardships.Screen;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Cosmetics;

/// <summary>
/// Магазин косметики и всё, что она меняет вне окон программы: скин курсора и эффекты курсора, кликов и покупок.
/// Темы и скины монет рисует приложение по <see cref="CosmeticsChanged"/>, питомца — модуль питомца.
/// Косметика только меняет вид и не трогает доход.
/// </summary>
public sealed class CosmeticsModule : IModule
{
    private readonly ConcurrentDictionary<string, HardshipInfo> _hardships = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _cursorHardships = new(StringComparer.Ordinal);
    private IModuleContext? _context;
    private PaletteArt? _palette;
    private CursorSkin? _skin;
    private EffectsPlayer? _effects;
    private volatile bool _blocked;

    public string Id => "cosmetics";

    public string Name => "Косметика";

    internal Wardrobe? Wardrobe { get; private set; }

    internal CursorSkin? Skin => _skin;

    internal EffectsPlayer? Effects => _effects;

    public void Start(IModuleContext context)
    {
        _context = context;
        _palette = PaletteArt.Load(context.Log);
        var items = CosmeticCatalog.Load(
            context.Data,
            context.Log,
            _palette.Keys.Keys.ToHashSet(),
            _palette.Names.Count > 0 ? _palette.Names.ToHashSet(StringComparer.Ordinal) : null);
        var wardrobe = new Wardrobe(items, context.Economy, context.Events, context.Saves, context.Settings, TimeProvider.System, context.Log);
        Wardrobe = wardrobe;
        _skin = new CursorSkin(context.Changes, context.Log);
        _effects = new EffectsPlayer(context.Log);

        var events = context.Events;
        events.Subscribe<CosmeticsChanged>(Apply);
        events.Subscribe<CosmeticsRequested>(_ => wardrobe.Publish());
        events.Subscribe<CosmeticPurchaseRequested>(request => wardrobe.Buy(request.Code));
        events.Subscribe<CosmeticEquipRequested>(request => wardrobe.Equip(request.Slot, request.Code));
        events.Subscribe<CosmeticGrantRequested>(request => wardrobe.Grant(request.Code, request.Source));
        events.Subscribe<CosmeticEffectsRequested>(request => wardrobe.SetEffects(request.Enabled));
        events.Subscribe<CosmeticsAvailableRequested>(_ => events.Publish(wardrobe.Available()));

        // Уровни открывают косметику: и та, что указана у звания, и предметы с unlockLevel.
        events.Subscribe<LevelReached>(reached =>
        {
            if (reached.Cosmetic is { } code)
            {
                wardrobe.Grant(code, $"rank.{reached.Rank}");
            }

            wardrobe.ReachLevel(reached.Level);
        });
        events.Subscribe<ProgressChanged>(change => wardrobe.ReachLevel(change.Snapshot.Level));

        // Усложнения вида курсора важнее скина и эффектов.
        events.Subscribe<HardshipOffered>(offer => _hardships[offer.Hardship.Id] = offer.Hardship);
        events.Subscribe<HardshipStartRequested>(request =>
        {
            if (_hardships.TryGetValue(request.Id, out var info) && info.Resources.HasFlag(HardshipResources.CursorLook))
            {
                _cursorHardships[request.Id] = 0;
                _skin.Pause(request.Id);
                UpdatePause();
            }
        });
        events.Subscribe<HardshipEnded>(ended =>
        {
            if (_cursorHardships.TryRemove(ended.Hardship.Id, out _))
            {
                _skin.Resume(ended.Hardship.Id);
                UpdatePause();
            }
        });
        events.Subscribe<HardshipStatus>(status =>
        {
            _blocked = status.Blocked is not null;
            UpdatePause();
        });

        events.Subscribe<MouseButtonChanged>(change =>
        {
            if (change is { Button: MouseButton.Left, Pressed: true, Injected: false })
            {
                _effects.Click(CursorControl.Position());
            }
        });
        events.Subscribe<UpgradePurchased>(_ => Celebrate());
        events.Subscribe<ItemUnlocked>(_ => Celebrate());
        events.Subscribe<CosmeticPurchased>(_ => Celebrate());

        context.Log.Info($"Косметики: {items.Count}, у игрока: {items.Count(item => wardrobe.Owns(item.Code))}");
        events.Publish(new HardshipsRequested());
        events.Publish(new HardshipStatusRequested());
        wardrobe.Publish();
        events.Publish(wardrobe.Available());
    }

    public void Stop()
    {
        _effects?.Dispose();
        _skin?.Dispose();
        _effects = null;
        _skin = null;
    }

    /// <summary>Рисует картинки предмета: кадры основной анимации.</summary>
    internal EffectArt? Art(CosmeticItem? item)
    {
        if (item is null || _palette is null)
        {
            return null;
        }

        var frames = item.Frames(CosmeticItem.DefaultAnimation).Select(_palette.Render).ToList();
        return frames.Count == 0 ? null : new EffectArt(frames, item.Fps);
    }

    private void Apply(CosmeticsChanged state)
    {
        var cursor = state.Equipped.GetValueOrDefault(CosmeticSlot.Cursor);
        _skin?.Set(cursor, Art(cursor)?.Frames[0]);
        if (state.EffectsEnabled)
        {
            _effects?.Configure(
                Art(state.Equipped.GetValueOrDefault(CosmeticSlot.Trail)),
                Art(state.Equipped.GetValueOrDefault(CosmeticSlot.Click)),
                Art(state.Equipped.GetValueOrDefault(CosmeticSlot.Purchase)));
        }
        else
        {
            _effects?.Configure(null, null, null);
        }
    }

    private void UpdatePause()
    {
        if (_effects is { } effects)
        {
            effects.Paused = _blocked || !_cursorHardships.IsEmpty;
        }
    }

    private void Celebrate() => _effects?.Celebrate(OverlayArea.WorkArea());
}
