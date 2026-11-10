using Mixer.Audio.Engine;
using Xunit.Abstractions;

namespace Mixer.Tests;

public class WireTests(ITestOutputHelper output)
{
    [Fact]
    public void Source_stall_counts_one_underrun_and_recovers()
    {
        var sim = new WireSimulation { SourceStall = (5, 0.2), KeepOutput = true };
        var wire = sim.Run(10);
        Assert.Equal(1, wire.Underruns);
        Assert.Equal(WireState.Running, wire.State);

        // фейд: на стыке нет скачка больше, чем даёт сам синус 1 кГц (шаг ≤ 2π·1000/48000·0,5 ≈ 0,065)
        var o = sim.Output;
        float maxStep = 0;
        for (int i = 2; i < o.Count; i += 2) maxStep = MathF.Max(maxStep, MathF.Abs(o[i] - o[i - 2]));
        output.WriteLine($"наибольший шаг между отсчётами: {maxStep:F4}");
        Assert.True(maxStep < 0.08f, $"щелчок: шаг {maxStep:F3}");
    }

    [Fact]
    public void Output_stall_drops_old_audio_back_to_target()
    {
        var sim = new WireSimulation { OutputStall = (5, 0.3) };
        var wire = sim.Run(12);
        Assert.Equal(1, wire.Overruns);
        Assert.Equal(0, wire.Underruns);
        var last = sim.Samples[^1];
        output.WriteLine($"после паузы выхода: заполнение {last.FillMs:F2} мс, ε {last.EpsPpm:F1} ppm");
        Assert.InRange(last.FillMs, 19, 21);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Source_burst_is_dropped_at_once_not_drained_for_seconds(bool flagged)
    {
        // этап 1, первый прогон: USB-микрофон после медленного старта отдал накопленное, задержка выросла на 60 мс
        var sim = new WireSimulation { SourceBurst = (5, 40), FlagBreaks = flagged };
        var wire = sim.Run(12);
        var after = sim.Samples.Where(s => s.Time > 6.5).ToList();
        output.WriteLine($"сбросов {wire.Resyncs}, заполнение после 6,5 с: {after.Min(s => s.FillMs):F2}…{after.Max(s => s.FillMs):F2} мс");
        Assert.Equal(1, wire.Resyncs);
        Assert.Equal(0, wire.Underruns);
        Assert.All(after, s => Assert.InRange(s.FillMs, 17, 23));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Short_source_gap_recovers_without_underrun(bool flagged)
    {
        // Пропал один пакет (10 мс): кольцо реально стало тоньше. Регулятор добирает недостачу; прямая оценка частоты
        // поначалу принимает потерю за медленный источник (−10 мс за окно), смещение рассасывается с ростом окна,
        // а медленный PI его компенсирует. Задержка отклоняется на несколько мс и возвращается, сбоев нет.
        var sim = new WireSimulation { SourceStall = (5, 0.01), FlagBreaks = flagged };
        var wire = sim.Run(240);
        var mid = sim.Samples.Where(s => s.Time > 22).ToList();
        var tail = sim.Samples.Where(s => s.Time > 180).ToList();
        output.WriteLine($"underrun {wire.Underruns}, сбросов {wire.Resyncs}; после 22 с: {mid.Min(s => s.FillMs):F2}…{mid.Max(s => s.FillMs):F2} мс, " +
                         $"ε {mid.Min(s => s.EpsPpm):F0}…{mid.Max(s => s.EpsPpm):F0}; после 180 с: {tail.Min(s => s.FillMs):F2}…{tail.Max(s => s.FillMs):F2} мс, ε до {tail.Max(s => Math.Abs(s.EpsPpm)):F0}");
        Assert.Equal(0, wire.Underruns);
        Assert.Equal(0, wire.Resyncs);
        Assert.All(mid, s => Assert.InRange(s.FillMs, 14, 26));
        Assert.All(tail, s => Assert.InRange(s.FillMs, 18, 22));
        Assert.All(tail, s => Assert.InRange(s.EpsPpm, -100, 100));
    }

    [Fact]
    public void Startup_latency_is_target_not_more()
    {
        var sim = new WireSimulation();
        var wire = sim.Run(3);
        double target = sim.Options.TargetPeriods * sim.Options.PeriodMs;
        output.WriteLine($"заполнение {wire.FillMs:F2} мс, задержка провода {wire.LatencyMs:F2} мс");
        Assert.InRange(wire.FillMs, target - 1, target + 1);
    }

    [Fact]
    public void App_pause_is_a_gap_not_an_underrun()
    {
        // приложение замолчало на 2 с (видео на паузе): у process loopback в это время нет пакетов
        var sim = new WireSimulation { Intermittent = true, SourceStall = (5, 2), KeepOutput = true };
        var wire = sim.Run(12);
        output.WriteLine($"underrun {wire.Underruns}, пауз {wire.Gaps}, сбросов {wire.Resyncs}, заполнение {wire.FillMs:F2} мс");
        Assert.Equal(0, wire.Underruns);
        Assert.Equal(1, wire.Gaps);
        Assert.Equal(0, wire.Resyncs);
        Assert.Equal(WireState.Running, wire.State);
        Assert.InRange(wire.FillMs, 18, 22);

        var o = sim.Output;
        float maxStep = 0;
        for (int i = 2; i < o.Count; i += 2) maxStep = MathF.Max(maxStep, MathF.Abs(o[i] - o[i - 2]));
        Assert.True(maxStep < 0.08f, $"щелчок: шаг {maxStep:F3}");
    }

    [Fact]
    public void Short_stall_of_app_source_is_still_an_underrun()
    {
        // пакет опоздал на 50 мс — это сбой, а не пауза приложения
        var sim = new WireSimulation { Intermittent = true, SourceStall = (5, 0.05) };
        var wire = sim.Run(10);
        Assert.Equal(1, wire.Underruns);
        Assert.Equal(0, wire.Gaps);
    }

    [Fact]
    public void Long_app_pause_does_not_spoil_drift_estimate()
    {
        // Минута тишины без вычета паузы выглядела бы как источник на −50% медленнее: ε ушла бы в предел ±2%.
        var sim = new WireSimulation { Intermittent = true, DriftPpm = 300, SourceStall = (60, 60) };
        var wire = sim.Run(240);
        var after = sim.Samples.Where(s => s.Time > 140).ToList();
        output.WriteLine($"дрейф {wire.DriftPpm:F0} ppm, ε {after.Min(s => s.EpsPpm):F0}…{after.Max(s => s.EpsPpm):F0}, " +
                         $"заполнение {after.Min(s => s.FillMs):F2}…{after.Max(s => s.FillMs):F2} мс, underrun {wire.Underruns}, пауз {wire.Gaps}, сбросов {wire.Resyncs}");
        Assert.Equal(0, wire.Underruns);
        Assert.Equal(1, wire.Gaps);
        Assert.Equal(0, wire.Resyncs);
        Assert.InRange(wire.DriftPpm, 200, 400);
        Assert.All(after, s => Assert.InRange(s.EpsPpm, 150, 450));
        Assert.All(after, s => Assert.InRange(s.FillMs, 16, 24));
    }

    [Fact]
    public void Device_source_stall_is_still_an_underrun()
    {
        // для устройств поведение этапа 1 не меняется: любая пауза — underrun
        var sim = new WireSimulation { SourceStall = (5, 2) };
        var wire = sim.Run(12);
        Assert.Equal(1, wire.Underruns);
        Assert.Equal(0, wire.Gaps);
    }

    [Fact]
    public void Writer_does_not_fill_ring_while_no_reader()
    {
        var clock = new SourceClock();
        clock.Started(48000);
        var wire = new Wire("a", "b", clock, new EngineOptions());
        var block = new float[960];
        for (int i = 0; i < 100; i++) wire.Write(block, 480, i);
        Assert.Equal(0, wire.Overruns);
    }
}
