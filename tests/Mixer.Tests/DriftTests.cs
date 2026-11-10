using Mixer.Audio.Engine;
using Xunit.Abstractions;

namespace Mixer.Tests;

public class DriftTests(ITestOutputHelper output)
{
    private void Dump(WireSimulation sim)
    {
        foreach (var s in sim.Samples.Where((_, i) => i % 10 == 0))
            output.WriteLine($"{s.Time,5:F1} с  ε {s.EpsPpm,8:F1} ppm  заполнение {s.FillMs,6:F2} мс  underrun {s.Underruns} overrun {s.Overruns}");
    }

    /// <summary>Время, после которого ε держится в пределах ±10% от дрейфа, а заполнение — в ±1 мс от цели.</summary>
    private static double SettleTime(WireSimulation sim, double driftPpm, double targetMs)
    {
        double tol = Math.Max(Math.Abs(driftPpm) * 0.1, 10);
        double settled = 0;
        foreach (var s in sim.Samples)
            if (Math.Abs(s.EpsPpm - driftPpm) > tol || Math.Abs(s.FillMs - targetMs) > 1.0)
                settled = s.Time;
        return settled;
    }

    [Theory]
    [InlineData(300, 48000, 48000)]
    [InlineData(-300, 48000, 48000)]
    [InlineData(300, 44100, 48000)]
    [InlineData(300, 48000, 44100)]
    public void PI_converges_within_10s_on_300ppm_drift(double drift, int fin, int fout)
    {
        var sim = new WireSimulation { InRate = fin, OutRate = fout, DriftPpm = drift };
        var wire = sim.Run(40);
        Dump(sim);
        double target = sim.Options.TargetPeriods * sim.Options.PeriodMs;
        double settle = SettleTime(sim, drift, target);
        output.WriteLine($"сошлось за {settle:F1} с");
        Assert.True(settle < 10, $"сошлось только за {settle:F1} с");
        Assert.Equal(0, wire.Underruns);
        Assert.Equal(0, wire.Overruns);
    }

    [Theory]
    // 44,1 кГц блоками по 441 кадру против 48 кГц блоками по 480, пробуждения запаздывают на 0…jitter мс
    [InlineData(1.0, 60)]
    [InlineData(3.0, 200)]   // стресс: такой джиттер у MMCSS-потоков бывает только под тяжёлой нагрузкой
    public void PI_holds_under_scheduler_jitter(double jitterMs, double maxDevPpm)
    {
        var sim = new WireSimulation { InRate = 44100, OutRate = 48000, DriftPpm = 300, JitterMs = jitterMs };
        var wire = sim.Run(90, seed: 7);
        Dump(sim);
        var tail = sim.Samples.Where(s => s.Time > 20).ToList();
        double maxDev = tail.Max(s => Math.Abs(s.EpsPpm - 300));
        output.WriteLine($"после 20 с: отклонение ε до {maxDev:F1} ppm, заполнение {tail.Min(s => s.FillMs):F2}…{tail.Max(s => s.FillMs):F2} мс");
        Assert.True(maxDev < maxDevPpm, $"ε гуляет на {maxDev:F1} ppm");
        Assert.Equal(0, wire.Underruns);
        Assert.Equal(0, wire.Overruns);
    }

    [Fact]
    public void Output_tone_stays_clean_while_regulating()
    {
        // Сигнал после провода с дрейфом и джиттером: ни щелчков, ни грязи от переменного коэффициента.
        // Подгонка с свободной частотой по окнам 100 мс: медленная подстройка ε (FM на десятки ppm) — не искажение.
        var sim = new WireSimulation { InRate = 44100, OutRate = 48000, DriftPpm = 300, JitterMs = 1, KeepOutput = true };
        var wire = sim.Run(30, seed: 3);
        var outp = sim.Output.ToArray();
        // после сходимости регулятора (во время неё ε меняется на сотни ppm — это ЧМ, а не искажение)
        double worst = double.NegativeInfinity;
        for (int start = 48000 * 12; start + 4800 <= outp.Length / 2; start += 4800)
            worst = Math.Max(worst, Signal.ThdN4(outp.AsSpan(start * 2, 4800 * 2), 1000, 48000));
        output.WriteLine($"THD+N на выходе провода, худшее окно 100 мс после 12 с: {worst:F1} дБ");
        // щелчков нет на всём прогоне после старта: шаг не больше, чем у самого синуса (2π·1000/48000·0,5)
        float maxStep = 0;
        for (int i = 2 * 4800; i < outp.Length; i += 2) maxStep = MathF.Max(maxStep, MathF.Abs(outp[i] - outp[i - 2]));
        output.WriteLine($"наибольший шаг между отсчётами: {maxStep:F4}");
        Assert.Equal(0, wire.Underruns);
        // сам ресемплер даёт ≈ −100 дБ; остаток до −80 — ЧМ от шума ε при джиттере 1 мс (десятки ppm, не слышно)
        Assert.True(worst < -75, $"THD+N {worst:F1} дБ");
        Assert.True(maxStep < 0.0660f, $"шаг {maxStep:F4}");
    }

    /// <summary>
    /// Пакетный источник, как USB-микрофон fifine на этапе 1: пакеты по 441 кадру строго каждые 10 мс,
    /// излишек от дрейфа +566 ppm — лишним пакетом раз в 17,7 с (DATA_DISCONTINUITY). Истинная задержка
    /// (от АЦП до чтения) должна стоять, ε — держаться около дрейфа, сбоев нет.
    /// </summary>
    [Theory]
    [InlineData(566, 0, 300, 60)]
    [InlineData(-566, 1, 300, 60)]   // первый пропуск пакета даёт один underrun, после него цель поднимается на пакет
    // при +150 ppm лишний пакет приходит раз в 67 с: дрейф виден по данным только после нескольких таких пакетов
    [InlineData(150, 0, 900, 400)]
    public void Packetized_source_holds_steady_latency(double drift, int allowedUnderruns, double seconds, double settled)
    {
        var sim = new WireSimulation { InRate = 44100, OutRate = 48000, DriftPpm = drift, Packetized = true, FlagBreaks = true, JitterMs = 0.5 };
        var wire = sim.Run(seconds, seed: 5);
        var tail = sim.Samples.Where(s => s.Time > settled).ToList();
        double lo = tail.Min(s => s.TrueLatencyMs), hi = tail.Max(s => s.TrueLatencyMs);
        output.WriteLine($"дрейф {drift:+0;-0}: ε {tail.Min(s => s.EpsPpm):F0}…{tail.Max(s => s.EpsPpm):F0} ppm, истинная задержка {lo:F1}…{hi:F1} мс, " +
                         $"underrun {wire.Underruns}, сбросов {wire.Resyncs}, overrun {wire.Overruns}");
        Assert.True(tail.All(s => s.Tracking), "регулятор не перешёл в слежение");
        Assert.True(wire.Underruns <= allowedUnderruns);
        Assert.Equal(0, wire.Resyncs);
        Assert.Equal(0, wire.Overruns);
        Assert.True(hi - lo < 5, $"задержка плывёт на {hi - lo:F1} мс"); // критерий спецификации: ±5 мс
        Assert.InRange(tail.Average(s => s.EpsPpm), drift - 100, drift + 100);
    }

    [Fact]
    public void Ppm_is_clamped_to_1000_while_acquiring()
    {
        var sim = new WireSimulation { DriftPpm = 3000 };
        sim.Run(20);
        Assert.All(sim.Samples.Where(s => !s.Tracking), s => Assert.InRange(s.EpsPpm, -1000.0001, 1000.0001));
    }

    /// <summary>VB-Cable на этапе 1: выход на 1% медленнее реального времени. После захвата провод следует за ним без потерь.</summary>
    [Theory]
    [InlineData(10200)]
    [InlineData(-10200)]
    public void Tracking_follows_a_one_percent_clock(double drift)
    {
        var sim = new WireSimulation { InRate = 44100, OutRate = 48000, DriftPpm = drift, JitterMs = 0.5 };
        var wire = sim.Run(180, seed: 2);
        var tail = sim.Samples.Where(s => s.Time > 60).ToList();
        int resyncsAt60 = sim.Samples.First(s => s.Time > 60).Resyncs;
        output.WriteLine($"ε {tail.Min(s => s.EpsPpm):F0}…{tail.Max(s => s.EpsPpm):F0} ppm, заполнение {tail.Min(s => s.FillMs):F1}…{tail.Max(s => s.FillMs):F1} мс, " +
                         $"сбросов за захват {resyncsAt60}, после {wire.Resyncs - resyncsAt60}, underrun {wire.Underruns}");
        Assert.True(tail.All(s => s.Tracking));
        Assert.Equal(resyncsAt60, wire.Resyncs);
        Assert.All(tail, s => Assert.InRange(s.EpsPpm, drift - 300, drift + 300));
        Assert.All(tail, s => Assert.InRange(s.FillMs, 15, 25));
    }

    [Fact]
    public void Strict_spec_limit_can_be_restored()
    {
        var sim = new WireSimulation { DriftPpm = 3000, Options = new EngineOptions { MaxDriftPpm = 1000 } };
        sim.Run(60);
        Assert.All(sim.Samples, s => Assert.InRange(s.EpsPpm, -1000.0001, 1000.0001));
    }
}
