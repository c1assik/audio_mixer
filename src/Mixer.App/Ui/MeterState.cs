namespace Mixer.App.Ui;

/// <summary>
/// Состояние стереоиндикатора: мгновенный подъём, спад 20 дБ/с, удержание пика 1,5 с (спецификация, «Усиление и
/// индикаторы»). Меняется в потоке интерфейса раз в кадр, рисуется <see cref="MeterBar"/> без привязок.
/// </summary>
public sealed class MeterState
{
    public const float FloorDb = -60f;
    public const float RedDb = -1f;
    private const float DecayDbPerSecond = 20f;
    private const double HoldSeconds = 1.5;

    public float L { get; private set; } = FloorDb;
    public float R { get; private set; } = FloorDb;
    public float HoldL { get; private set; } = FloorDb;
    public float HoldR { get; private set; } = FloorDb;
    /// <summary>Сколько дБ срезал ограничитель (0 — не работал), с тем же удержанием.</summary>
    public float ReductionDb { get; private set; }

    private double _holdLAt, _holdRAt, _reductionAt;

    /// <summary>Растёт, когда картинка изменилась.</summary>
    public int Version { get; private set; }

    /// <summary>Уровень 0…1 по громкому каналу (толщина провода).</summary>
    public double Level => Math.Clamp((Math.Max(L, R) - FloorDb) / -FloorDb, 0, 1);

    public static float ToDb(float amplitude) => amplitude <= 1e-6f ? FloorDb : Math.Max(FloorDb, 20f * MathF.Log10(amplitude));

    public void Push(float peakL, float peakR, double now, double dt)
    {
        float l0 = L, r0 = R, hl0 = HoldL, hr0 = HoldR;
        float fall = (float)(DecayDbPerSecond * dt);
        (L, HoldL, _holdLAt) = Step(ToDb(peakL), L, HoldL, _holdLAt, now, fall);
        (R, HoldR, _holdRAt) = Step(ToDb(peakR), R, HoldR, _holdRAt, now, fall);
        if (Math.Abs(L - l0) > 0.05f || Math.Abs(R - r0) > 0.05f || HoldL != hl0 || HoldR != hr0) Version++;
    }

    private static (float Level, float Hold, double HoldAt) Step(float db, float level, float hold, double holdAt, double now, float fall)
    {
        level = Math.Max(db, level - fall);
        if (db >= hold)
        {
            hold = db;
            holdAt = now;
        }
        else if (now - holdAt > HoldSeconds)
        {
            hold = Math.Max(level, hold - fall);
        }
        return (Math.Max(FloorDb, level), Math.Max(FloorDb, hold), holdAt);
    }

    /// <param name="minGain">Минимальное усиление ограничителя с прошлого опроса (1 — не работал).</param>
    public void PushReduction(float minGain, double now)
    {
        float r0 = ReductionDb;
        float db = minGain < 0.999f ? -20f * MathF.Log10(Math.Max(minGain, 1e-4f)) : 0f;
        if (db >= ReductionDb)
        {
            ReductionDb = db;
            _reductionAt = now;
        }
        else if (now - _reductionAt > HoldSeconds)
        {
            ReductionDb = db;
        }
        if (Math.Abs(ReductionDb - r0) > 0.05f) Version++;
    }

    public void Reset()
    {
        if (L == FloorDb && R == FloorDb && HoldL == FloorDb && HoldR == FloorDb && ReductionDb == 0) return;
        L = R = HoldL = HoldR = FloorDb;
        ReductionDb = 0;
        Version++;
    }
}
