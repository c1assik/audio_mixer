using Mixer.Audio.Engine;
using Xunit.Abstractions;

namespace Mixer.Tests;

/// <summary>Перебор коэффициентов регулятора. Не тест: запускать вручную (--filter TuningSweep).</summary>
public class TuningSweep(ITestOutputHelper output)
{
    [Fact(Skip = "ручной перебор")]
    public void Sweep()
    {
        foreach (double kp in new[] { 0.3, 0.5, 0.8 })
        foreach (double kiRatio in new[] { 0.1, 0.2 })
        foreach (double bw in new[] { 0.05, 0.1, 0.2 })
        {
            var opt = new EngineOptions { Kp = kp, Ki = kp * kiRatio, TimelineBandwidthHz = bw };
            var clean = new WireSimulation { InRate = 44100, OutRate = 48000, DriftPpm = 300, Options = opt };
            clean.Run(40);
            double settle = 0;
            foreach (var s in clean.Samples)
                if (Math.Abs(s.EpsPpm - 300) > 30 || Math.Abs(s.FillMs - 20) > 1) settle = s.Time;
            string Dev(double jit)
            {
                var sim = new WireSimulation { InRate = 44100, OutRate = 48000, DriftPpm = 300, JitterMs = jit, Options = opt };
                var w = sim.Run(90, seed: 7);
                var tail = sim.Samples.Where(s => s.Time > 20).ToList();
                return $"{tail.Max(s => Math.Abs(s.EpsPpm - 300)),6:F1} ppm (u{w.Underruns})";
            }
            output.WriteLine($"Kp {kp:F2} Ki {kp * kiRatio:F3} DLL {bw:F2} Гц: сходимость {settle,5:F1} с; джиттер 0.5 мс {Dev(0.5)}; 1 мс {Dev(1)}; 3 мс {Dev(3)}");
        }
    }
}

public class TrackingSweep(ITestOutputHelper output)
{
    [Fact(Skip = "ручной перебор")]
    public void Sweep()
    {
        foreach (double kp in new[] { 0.02, 0.05, 0.1 })
        foreach (double ki in new[] { 0.0003, 0.001, 0.003 })
        foreach (double tau in new[] { 2.0, 5.0 })
        {
            var opt = new EngineOptions { TrackKp = kp, TrackKi = ki, TrackSmoothingSeconds = tau };
            string Run(double drift)
            {
                var sim = new WireSimulation { InRate = 44100, OutRate = 48000, DriftPpm = drift, Packetized = true, FlagBreaks = true, JitterMs = 0.5, Options = opt };
                var w = sim.Run(300, seed: 5);
                var tail = sim.Samples.Where(s => s.Time > 60).ToList();
                return $"ε {tail.Min(s => s.EpsPpm),6:F0}…{tail.Max(s => s.EpsPpm),5:F0}, задержка {tail.Min(s => s.TrueLatencyMs):F1}…{tail.Max(s => s.TrueLatencyMs):F1} мс, u{w.Underruns} r{w.Resyncs} o{w.Overruns}{(tail.All(s => s.Tracking) ? "" : " (не в слежении)")}";
            }
            output.WriteLine($"Kp {kp:F2} Ki {ki:F4} τ {tau:F0}: +566 [{Run(566)}]  −566 [{Run(-566)}]");
        }
    }
}
