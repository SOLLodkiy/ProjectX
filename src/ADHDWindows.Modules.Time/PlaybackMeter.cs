namespace ADHDWindows.Modules.Time;

/// <summary>
/// Снимок плеера. <paramref name="Playing"/> — идёт воспроизведение со звуком.
/// <paramref name="Position"/> и <paramref name="Duration"/> в секундах, null — плеер их не сообщает.
/// <paramref name="Identity"/> — что играет: по нему опознаются повторы.
/// </summary>
public sealed record PlaybackSample(double? Position, double? Duration, bool Playing, double Rate, string Identity);

/// <summary>
/// Итог нового снимка. <paramref name="Credit"/> — засчитанные секунды реального времени с прошлого снимка.
/// <paramref name="NewPlay"/> — началось новое воспроизведение: другой ролик или тот же с начала.
/// <paramref name="Completed"/> — ролик только что досмотрен почти полностью.
/// </summary>
public readonly record struct MeterStep(double Credit, bool NewPlay, bool Completed);

/// <summary>
/// Считает реально просмотренное время одного плеера по снимкам. Засчитывается время между снимками, за которое
/// позиция продвинулась без скачков. Пауза не двигает позицию, перемотка двигает её скачком, а ускорение
/// двигает быстрее, но засчитывается всё равно реальное время. Не потокобезопасен.
/// </summary>
public sealed class PlaybackMeter(double completionShare, double minCompletionSeconds)
{
    /// <summary>Промежуток без снимков длиннее этого не засчитывается: неизвестно, что в нём было.</summary>
    public const double MaxGapSeconds = 10;

    /// <summary>Запас на неровную доставку снимков и буферизацию.</summary>
    private const double SeekSlack = 1;

    /// <summary>Позиция у начала, с которой ролик считается начатым заново.</summary>
    private const double RestartPosition = 10;

    private const double MinRate = 1d / 16;

    /// <summary>
    /// Сколько после перемотки ничего не засчитывается. Иначе перемотка каждую секунду приносила бы
    /// доход за кусочки между скачками.
    /// </summary>
    public static readonly TimeSpan SeekQuarantine = TimeSpan.FromSeconds(3);

    private DateTimeOffset _quietUntil = DateTimeOffset.MinValue;

    private PlaybackSample? _last;
    private DateTimeOffset _lastAt;

    /// <summary>Просмотрено секунд ролика в текущем воспроизведении, без перемотки.</summary>
    public double Covered { get; private set; }

    public bool Completed { get; private set; }

    public MeterStep Feed(PlaybackSample sample, DateTimeOffset now)
    {
        var last = _last;
        var lastAt = _lastAt;
        _last = sample;
        _lastAt = now;

        if (last is null || IsNewPlay(last, sample))
        {
            Covered = 0;
            Completed = false;
            return new MeterStep(0, NewPlay: true, Completed: false);
        }

        var elapsed = (now - lastAt).TotalSeconds;
        var credit = 0d;
        if (elapsed > 0 && elapsed <= MaxGapSeconds && (last.Playing || sample.Playing))
        {
            if (last.Position is { } from && sample.Position is { } to)
            {
                var moved = to - from;
                var rate = Math.Max(MinRate, Math.Max(last.Rate, sample.Rate));
                if (moved >= 0 && moved <= (elapsed * rate * 1.5) + SeekSlack)
                {
                    credit = Math.Min(elapsed, moved / rate);
                    if (lastAt < _quietUntil)
                    {
                        // Засчитывается только часть промежутка после паузы за перемотку.
                        var allowed = Math.Max(0, (now - _quietUntil).TotalSeconds);
                        credit = Math.Min(credit, allowed);
                        moved = Math.Min(moved, allowed * rate);
                    }

                    Covered += moved;
                }
                else
                {
                    _quietUntil = now + SeekQuarantine;
                }
            }
            else if (last.Playing && sample.Playing)
            {
                // Плеер без позиции: остаётся только реальное время воспроизведения.
                credit = elapsed;
            }
        }

        var completed = false;
        if (!Completed
            && sample.Duration is { } duration
            && double.IsFinite(duration)
            && duration >= minCompletionSeconds
            && Covered >= duration * completionShare)
        {
            Completed = completed = true;
        }

        return new MeterStep(credit, NewPlay: false, completed);
    }

    private static bool IsNewPlay(PlaybackSample last, PlaybackSample sample)
    {
        if (last.Identity != sample.Identity)
        {
            return true;
        }

        if (last.Duration is { } before && sample.Duration is { } after
            && double.IsFinite(before) && double.IsFinite(after) && Math.Abs(before - after) > 1)
        {
            return true;
        }

        // Тот же ролик с начала: повтор или зацикленное видео.
        return last.Position is { } from && sample.Position is { } to
            && to < RestartPosition && to + 3 < from;
    }
}
