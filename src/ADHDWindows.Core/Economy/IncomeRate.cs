namespace ADHDWindows.Core.Economy;

/// <summary>Скользящая сумма за минуту с точностью до секунды. Потоками управляет владелец.</summary>
internal sealed class IncomeRate(TimeProvider clock)
{
    private const int WindowSeconds = 60;

    private readonly long[] _buckets = new long[WindowSeconds];
    private long _second = clock.GetUtcNow().ToUnixTimeSeconds();

    public long Total
    {
        get
        {
            Advance();
            var total = 0L;
            foreach (var bucket in _buckets)
            {
                total = bucket > long.MaxValue - total ? long.MaxValue : total + bucket;
            }

            return total;
        }
    }

    public void Add(long amount)
    {
        Advance();
        var index = (int)(_second % WindowSeconds);
        _buckets[index] = amount > long.MaxValue - _buckets[index] ? long.MaxValue : _buckets[index] + amount;
    }

    private void Advance()
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (now <= _second)
        {
            // Перевод часов назад не должен затирать уже посчитанные секунды.
            return;
        }

        var passed = Math.Min(now - _second, WindowSeconds);
        for (var offset = 1; offset <= passed; offset++)
        {
            _buckets[(_second + offset) % WindowSeconds] = 0;
        }

        _second = now;
    }
}
