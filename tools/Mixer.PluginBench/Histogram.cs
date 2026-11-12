using System.Diagnostics;

namespace Mixer.PluginBench;

/// <summary>
/// Гистограмма длительностей без аллокаций при записи: шаг 0,1 мкс до 2 мс, 10 мкс до 200 мс, дальше — переполнение.
/// Пишет один поток; читать из другого можно только для прогресса (значения могут быть чуть несогласованы).
/// </summary>
internal sealed class Histogram
{
    private const int FineBuckets = 20_000;   // 0,1 мкс × 20 000 = 2 мс
    private const int CoarseBuckets = 19_800; // 10 мкс × 19 800 = ещё 198 мс
    private static readonly double s_usPerTick = 1e6 / Stopwatch.Frequency;

    private readonly long[] _fine = new long[FineBuckets];
    private readonly long[] _coarse = new long[CoarseBuckets];
    private long _overflow;
    private double _sumUs;

    public long Count { get; private set; }
    public double MaxUs { get; private set; }
    public double MeanUs => Count == 0 ? 0 : _sumUs / Count;

    public void RecordTicks(long ticks) => RecordUs(ticks * s_usPerTick);

    public void RecordUs(double us)
    {
        if (us < 0) us = 0;
        Count++;
        _sumUs += us;
        if (us > MaxUs) MaxUs = us;
        int fine = (int)(us * 10);
        if (fine < FineBuckets) { _fine[fine]++; return; }
        int coarse = (int)((us - 2000) / 10);
        if (coarse < CoarseBuckets) { _coarse[coarse]++; return; }
        _overflow++;
    }

    /// <summary>Сколько значений строго больше порога (с точностью до корзины).</summary>
    public long CountAbove(double us)
    {
        long n = 0;
        int fine = (int)(us * 10) + 1;
        for (int i = Math.Max(fine, 0); i < FineBuckets; i++) n += _fine[i];
        int coarseFrom = us < 2000 ? 0 : (int)((us - 2000) / 10) + 1;
        for (int i = coarseFrom; i < CoarseBuckets; i++) n += _coarse[i];
        return n + _overflow;
    }

    /// <summary>Верхняя граница корзины, в которую попал p-й перцентиль (p от 0 до 100).</summary>
    public double Percentile(double p)
    {
        if (Count == 0) return 0;
        long rank = (long)Math.Ceiling(Count * p / 100.0);
        if (rank < 1) rank = 1;
        long seen = 0;
        for (int i = 0; i < FineBuckets; i++)
        {
            seen += _fine[i];
            if (seen >= rank) return Math.Min((i + 1) / 10.0, MaxUs);
        }
        for (int i = 0; i < CoarseBuckets; i++)
        {
            seen += _coarse[i];
            if (seen >= rank) return Math.Min(2000 + (i + 1) * 10.0, MaxUs);
        }
        return MaxUs;
    }

    public object Summary() => new
    {
        count = Count,
        mean = Math.Round(MeanUs, 2),
        p50 = Percentile(50),
        p90 = Percentile(90),
        p99 = Percentile(99),
        p999 = Percentile(99.9),
        p9999 = Percentile(99.99),
        max = Math.Round(MaxUs, 1),
    };
}
