using System.Diagnostics;
using Mixer.Audio.Engine;

namespace Mixer.Tests;

/// <summary>
/// Дискретно-событийная модель провода: источник пишет блоки по своим часам, выход читает по своим.
/// Время синтетическое (тики Stopwatch), так что прогон на минуты занимает доли секунды.
/// </summary>
internal sealed class WireSimulation
{
    /// <param name="TrueLatencyMs">Сколько ждёт звук от АЦП до чтения: кольцо + ещё не отданное источником.</param>
    public sealed record Sample(double Time, double EpsPpm, double FillMs, int Underruns, int Overruns, int Resyncs, double TrueLatencyMs = 0, bool Tracking = false);

    public int InRate { get; init; } = 48000;
    public int OutRate { get; init; } = 48000;
    /// <summary>Дрейф часов источника относительно номинала, ppm (плюс — источник спешит).</summary>
    public double DriftPpm { get; init; }
    public double SourceBlockMs { get; init; } = 10;
    public double OutputBlockMs { get; init; } = 10;
    /// <summary>Случайное запаздывание пробуждения потоков, мс (0…jitter).</summary>
    public double JitterMs { get; init; }
    public double FreqHz { get; init; } = 1000;
    public EngineOptions Options { get; init; } = new();
    /// <summary>Пауза выхода (сек от начала, длительность сек) — имитация зависшего устройства.</summary>
    public (double At, double Length)? OutputStall { get; init; }
    /// <summary>Пауза источника.</summary>
    public (double At, double Length)? SourceStall { get; init; }
    /// <summary>Всплеск: в момент At источник отдаёт сверх нормы ещё Ms миллисекунд (накопленное).</summary>
    public (double At, double Ms)? SourceBurst { get; init; }
    /// <summary>Отмечать паузу и всплеск флагом разрыва, как WASAPI (DATA_DISCONTINUITY).</summary>
    public bool FlagBreaks { get; init; }
    /// <summary>
    /// Пакетный источник, как USB-микрофон на этапе 1: пакеты по таймеру с номинальным периодом,
    /// излишек от дрейфа — лишним пакетом подряд, недостача — пропущенным пакетом.
    /// </summary>
    public bool Packetized { get; init; }
    /// <summary>Прерывистый источник (process loopback): паузы — не сбой.</summary>
    public bool Intermittent { get; init; }

    public List<Sample> Samples { get; } = [];
    public List<float> Output { get; } = [];
    public bool KeepOutput { get; init; }

    /// <summary>
    /// Сообщать проводу периоды концов (SourceBlockMs и OutputBlockMs), как это делают узлы с «Низкой задержкой».
    /// false — период «обычный» (0), цель заполнения считается от EngineOptions.PeriodMs, как в v1.
    /// </summary>
    public bool ReportPeriods { get; init; }

    public Wire Run(double seconds, int seed = 1)
    {
        var rnd = new Random(seed);
        var clock = new SourceClock { Intermittent = Intermittent };
        clock.Started(InRate, ReportPeriods ? SourceBlockMs / 1000 : 0);
        var wire = new Wire("src", "out", clock, Options);

        double srcRate = InRate * (1 + DriftPpm * 1e-6);
        int srcBlock = (int)Math.Round(InRate * SourceBlockMs / 1000);
        int outBlock = (int)Math.Round(OutRate * OutputBlockMs / 1000);
        double srcPeriod = Packetized ? (double)srcBlock / InRate : srcBlock / srcRate; // период событий источника
        long delivered = 0;
        double Produced(double time) => time * srcRate + srcBlock * 1.5; // кадры, уже снятые АЦП
        double outPeriod = (double)outBlock / OutRate;

        var srcBuf = new float[srcBlock * 2];
        bool burstDone = false, stallFlagged = false;
        var mix = new float[outBlock * 2];
        var scratch = new float[outBlock * 2];
        long srcFrame = 0;
        double phaseStep = 2 * Math.PI * FreqHz / InRate;

        double nextSrc = 0, nextOut = 0, nextSample = 0;
        int srcIndex = 0, outIndex = 0;
        while (true)
        {
            // идеальные моменты событий + запаздывание пробуждения
            double tSrc = srcIndex * srcPeriod + Jitter(rnd);
            double tOut = outIndex * outPeriod + Jitter(rnd);
            nextSrc = Math.Max(nextSrc, tSrc);
            nextOut = Math.Max(nextOut, tOut);
            double t = Math.Min(nextSrc, nextOut);
            if (t > seconds) break;
            long ticks = (long)(t * Stopwatch.Frequency);

            if (nextSrc <= nextOut)
            {
                srcIndex++;
                if (SourceBurst is { } b && !burstDone && t >= b.At)
                {
                    burstDone = true;
                    if (FlagBreaks) clock.Break();
                    int extra = (int)(InRate * b.Ms / 1000);
                    var burst = new float[extra * 2];
                    for (int i = 0; i < extra; i++)
                    {
                        float v = (float)(0.5 * Math.Sin(phaseStep * srcFrame++));
                        burst[2 * i] = v;
                        burst[2 * i + 1] = v;
                    }
                    wire.Write(burst, extra, ticks);
                }
                int packets = 1;
                if (Packetized)
                {
                    double avail = Produced((srcIndex - 1) * srcPeriod) - delivered;
                    packets = avail >= 2 * srcBlock ? 2 : avail < srcBlock ? 0 : 1;
                    if (packets != 1 && FlagBreaks) clock.Break();
                }
                for (int p = 0; p < packets; p++)
                if (!InStall(SourceStall, t))
                {
                    delivered += srcBlock;
                    if (stallFlagged || (SourceStall is { } st && t >= st.At + st.Length && FlagBreaks && !stallFlagged))
                    {
                        if (!stallFlagged) clock.Break();
                        stallFlagged = true;
                    }
                    for (int i = 0; i < srcBlock; i++)
                    {
                        float v = (float)(0.5 * Math.Sin(phaseStep * srcFrame++));
                        srcBuf[2 * i] = v;
                        srcBuf[2 * i + 1] = v;
                    }
                    wire.Write(srcBuf, srcBlock, ticks);
                }
                else
                {
                    srcFrame += srcBlock;
                }
            }
            else
            {
                outIndex++;
                if (InStall(OutputStall, t)) continue;
                Array.Clear(mix);
                wire.Render(mix, scratch, outBlock, OutRate, ticks, ReportPeriods ? outPeriod : 0);
                if (KeepOutput) Output.AddRange(mix);
                if (t >= nextSample)
                {
                    double backlog = Packetized ? (Produced(t) - delivered) * 1000.0 / InRate : 0;
                    Samples.Add(new Sample(t, wire.EpsilonPpm, wire.FillMs, wire.Underruns, wire.Overruns, wire.Resyncs,
                        wire.RawFillMs + backlog, wire.Tracking));
                    nextSample += 0.1;
                }
            }
        }
        return wire;
    }

    private double Jitter(Random rnd) => JitterMs <= 0 ? 0 : rnd.NextDouble() * JitterMs / 1000;

    private static bool InStall((double At, double Length)? stall, double t) =>
        stall is { } s && t >= s.At && t < s.At + s.Length;
}
