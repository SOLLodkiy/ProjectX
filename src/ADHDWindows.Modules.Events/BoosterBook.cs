using System.Globalization;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hud;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.RandomEvents;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Modules.Events;

/// <summary>
/// Временные множители дохода: «Час удачи», бустеры из сундуков, колеса и у торговца. Множитель стоит на шаге
/// улучшений общей формулы дохода, поэтому режим усложнений действует и на него. Бустеры хранятся в сохранении
/// и после перезапуска продолжают действовать до своего конца. Потокобезопасна.
/// </summary>
public sealed class BoosterBook : IDisposable
{
    public const string SaveKey = "boosters";
    public const decimal MaxMultiplier = 10;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(2);

    private readonly IEconomy _economy;
    private readonly IEventBus _events;
    private readonly IKeyValueStore _saves;
    private readonly TextCatalog _texts;
    private readonly TimeProvider _clock;
    private readonly ILog _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (BoosterView View, IDisposable Modifier)> _active = new(StringComparer.Ordinal);
    private string? _hudText;
    private bool _hudPublished;

    public BoosterBook(IEconomy economy, IEventBus events, IKeyValueStore saves, TextCatalog texts, TimeProvider clock, ILog log)
    {
        _economy = economy;
        _events = events;
        _saves = saves;
        _texts = texts;
        _clock = clock;
        _log = log;
        var now = clock.GetUtcNow();
        foreach (var view in saves.Get<List<BoosterView>>(SaveKey) ?? [])
        {
            if (view.Ends > now && view.Multiplier > 0 && !string.IsNullOrEmpty(view.Id) && view.Ends - now <= MaxDuration)
            {
                _active[view.Id] = (view, Add(view));
            }
        }

        Save();
    }

    public IReadOnlyList<BoosterView> Active
    {
        get
        {
            lock (_lock)
            {
                return [.. _active.Values.Select(entry => entry.View).OrderBy(view => view.Ends)];
            }
        }
    }

    /// <summary>Выдаёт бустер или продлевает бустер с тем же кодом.</summary>
    public void Grant(BoosterGrantRequested request)
    {
        if (string.IsNullOrEmpty(request.Id) || request.Multiplier <= 0 || request.Duration <= TimeSpan.Zero)
        {
            _log.Warn($"Неверный бустер пропущен: {request}");
            return;
        }

        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            var multiplier = Math.Min(request.Multiplier, MaxMultiplier);
            var start = now;
            if (_active.Remove(request.Id, out var old))
            {
                old.Modifier.Dispose();
                start = old.View.Ends > now ? old.View.Ends : now;
                multiplier = Math.Max(multiplier, old.View.Multiplier);
            }

            var ends = start + request.Duration;
            if (ends - now > MaxDuration)
            {
                ends = now + MaxDuration;
            }

            var view = new BoosterView(request.Id, multiplier, request.Source, ends);
            _active[request.Id] = (view, Add(view));
            Save();
            _log.Info($"Бустер {request.Id} ×{multiplier.ToString(CultureInfo.InvariantCulture)} до {ends.ToLocalTime():HH:mm:ss}");
        }

        Publish();
        Tick();
    }

    /// <summary>Снимает закончившиеся бустеры и обновляет строку HUD. Вызывается раз в секунду.</summary>
    public void Tick()
    {
        var now = _clock.GetUtcNow();
        var changed = false;
        string? text;
        lock (_lock)
        {
            foreach (var id in _active.Where(pair => pair.Value.View.Ends <= now).Select(pair => pair.Key).ToList())
            {
                _active.Remove(id, out var gone);
                gone.Modifier.Dispose();
                changed = true;
                _log.Info($"Бустер закончился: {id}");
            }

            if (changed)
            {
                Save();
            }

            text = HudText(now);
            if (_hudPublished && text == _hudText)
            {
                text = null;
            }
            else
            {
                _hudText = text;
                _hudPublished = true;
                text ??= string.Empty;
            }
        }

        if (changed)
        {
            Publish();
        }

        if (text is not null)
        {
            _events.Publish(new HudStatusChanged(HudSlot.Event, text.Length == 0 ? null : text));
        }
    }

    public void Publish() => _events.Publish(new BoostersChanged(Active));

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var entry in _active.Values)
            {
                entry.Modifier.Dispose();
            }

            _active.Clear();
        }
    }

    private IDisposable Add(BoosterView view) =>
        _economy.AddModifier(new IncomeModifier(IncomeStep.Upgrade, view.Multiplier, view.Source));

    private void Save() => _saves.Set(SaveKey, _active.Values.Select(entry => entry.View).ToList());

    private string? HudText(DateTimeOffset now)
    {
        var views = _active.Values.Select(entry => entry.View).OrderBy(view => view.Ends).ToList();
        if (views.Count == 0)
        {
            return null;
        }

        var first = views[0];
        var left = first.Ends - now;
        var time = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : left.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        var text = _texts.Get(
            "events.hud.booster",
            _texts.Get($"events.booster.{first.Id}"),
            first.Multiplier.ToString("0.##", CultureInfo.CurrentCulture),
            time);
        return views.Count > 1 ? $"{text} +{views.Count - 1}" : text;
    }
}
