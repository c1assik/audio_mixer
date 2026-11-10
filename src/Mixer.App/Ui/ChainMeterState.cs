using Mixer.Audio.Dsp;

namespace Mixer.App.Ui;

/// <summary>
/// Индикаторы цепочки обработки узла в потоке интерфейса: уровень до и после обработки, подавление компрессора
/// (с удержанием 0,5 с, чтобы число на карточке не мигало) и гейт. Обновляется раз в кадр из
/// <see cref="ChainMeters"/> всех экземпляров цепочки узла.
/// </summary>
public sealed class ChainMeterState
{
    private const double ReductionHoldSeconds = 0.5;
    private const float ReductionFallDbPerSecond = 12f;

    public MeterState In { get; } = new();
    public MeterState Out { get; } = new();
    /// <summary>Подавление компрессора, дБ (положительное).</summary>
    public float ReductionDb { get; private set; }
    /// <summary>Гейт открыт (null — выключен).</summary>
    public bool? GateOpen { get; private set; }
    /// <summary>Огибающая детектора гейта, dBFS.</summary>
    public float GateLevelDb { get; private set; } = MeterState.FloorDb;
    /// <summary>Растёт, когда картинка изменилась.</summary>
    public int Version { get; private set; }

    private double _reductionAt;

    public void Push(ProcessingChain[] chains, double now, double dt)
    {
        float il = 0, ir = 0, ol = 0, or = 0, red = 0, gate = 0;
        bool? open = null;
        foreach (var c in chains)
        {
            var m = c.Meters;
            var (a, b) = m.In.Take();
            il = Math.Max(il, a);
            ir = Math.Max(ir, b);
            (a, b) = m.Out.Take();
            ol = Math.Max(ol, a);
            or = Math.Max(or, b);
            red = Math.Max(red, m.TakeReduction());
            gate = Math.Max(gate, m.TakeGateLevel());
            if (m.GateOpen is { } g) open = (open ?? false) || g;
        }
        int v = In.Version + Out.Version;
        In.Push(il, ir, now, dt);
        Out.Push(ol, or, now, dt);

        float r0 = ReductionDb;
        if (red >= ReductionDb)
        {
            ReductionDb = red;
            _reductionAt = now;
        }
        else if (now - _reductionAt > ReductionHoldSeconds)
        {
            ReductionDb = Math.Max(red, ReductionDb - (float)(ReductionFallDbPerSecond * dt));
        }
        float g0 = GateLevelDb;
        GateLevelDb = Math.Max(MeterState.ToDb(gate), GateLevelDb - (float)(20 * dt));
        bool changed = In.Version + Out.Version != v || Math.Abs(ReductionDb - r0) > 0.05f || Math.Abs(GateLevelDb - g0) > 0.1f || open != GateOpen;
        GateOpen = open;
        if (changed) Version++;
    }

    public void Reset()
    {
        In.Reset();
        Out.Reset();
        if (ReductionDb == 0 && GateOpen is null && GateLevelDb == MeterState.FloorDb) return;
        ReductionDb = 0;
        GateOpen = null;
        GateLevelDb = MeterState.FloorDb;
        Version++;
    }
}
