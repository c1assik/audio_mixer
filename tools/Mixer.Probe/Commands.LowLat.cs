using System.Diagnostics;
using System.Runtime.InteropServices;
using Mixer.Probe.Audio;
using Mixer.Probe.Interop;
using Mixer.Probe.Sys;

namespace Mixer.Probe;

internal static partial class Commands
{
    /// <summary>
    /// v2, этап 0, замер 4: IAudioClient3 — какие периоды движка даёт драйвер и держит ли поток минимальный период без сбоев.
    /// --list только спрашивает периоды (потоки не открываются). С --device открывает событийный поток
    /// через InitializeSharedAudioStream и считает пробуждения, их интервалы и сбои; --compare добавляет прогон обычного Initialize.
    /// </summary>
    public static int LowLat(Args a)
    {
        if (a.Flag("list")) return LowLatList();

        bool capture = a.Flag("capture");
        var eps = Devices.List(capture ? EDataFlow.Capture : EDataFlow.Render);
        var dev = Safety.Check(Devices.Find(eps, a.Req("device")));
        double seconds = a.Dbl("seconds", 30);
        string period = a.Opt("period") ?? "min";
        double? level = a.Opt("level") is null ? null : a.Dbl("level", -70);

        Console.WriteLine($"lowlat: {(capture ? "запись" : "воспроизведение")} «{dev.Name}», {seconds} с, период {period}" +
                          (capture ? "" : level is null ? ", тишина" : $", синус 1 кГц {Ui.Db(level.Value)} dBFS"));
        var results = new List<LowLatResult> { RunLowLat(dev, capture, seconds, period, level, lowLatency: true) };
        if (a.Flag("compare") && !Ui.Cancel.IsSet) results.Add(RunLowLat(dev, capture, seconds, period, level, lowLatency: false));

        Ui.Title("Итог");
        Console.WriteLine($"  {"режим",-26} {"период",12} {"буфер",12} {"латентн.",9} {"интервал ср/p99/макс, мс",26} {"сбоев",6} {"макс/мин",9} {"тайм-ауты",10} {"CPU %ядра",10}");
        foreach (var r in results)
            Console.WriteLine($"  {r.Mode,-26} {r.PeriodText,12} {r.BufferText,12} {(r.LatencyMs > 0 ? Ui.Num(r.LatencyMs, "F2") : "н/д"),9} " +
                              $"{$"{Ui.Num(r.Mean, "F2")}/{Ui.Num(r.P99, "F2")}/{Ui.Num(r.Max, "F1")}",26} {r.Glitches,6} {r.WorstMinute,9} {r.Timeouts,10} {Ui.Num(r.CpuCore, "F2"),10}");
        Console.WriteLine(capture
            ? "  Сбой записи — пакет с DATA_DISCONTINUITY или скачок позиции устройства."
            : "  Сбой воспроизведения — на пробуждении буфер был пуст (padding = 0): устройство успело всё доиграть.");
        Console.WriteLine("  Порог автоотката из спецификации: больше 3 сбоев за минуту. «латентн.» — GetStreamLatency, н/д — драйвер вернул 0.");
        bool ok = results[0].Glitches >= 0 && results[0].WorstMinute <= 3 && results[0].Error is null;
        if (results[0].Error is { } err) Ui.Warn(err);
        Console.WriteLine(ok ? "RESULT=OK" : "RESULT=GLITCHES");
        return ok ? 0 : 4;
    }

    private sealed record LowLatResult(string Mode, string PeriodText, string BufferText, double LatencyMs, double Mean, double P99, double Max,
        int Glitches, int WorstMinute, int Timeouts, double CpuCore, string? Error);

    private static int LowLatList()
    {
        foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
        {
            Ui.Title(flow == EDataFlow.Render ? "Воспроизведение: периоды движка IAudioClient3, кадров (мс)" : "Запись: периоды движка IAudioClient3, кадров (мс)");
            Console.WriteLine($"  {"устройство",-44} {"формат",-24} {"по умолч.",14} {"шаг",6} {"минимум",14} {"максимум",8} {"сейчас",14}");
            foreach (var ep in Devices.List(flow))
            {
                string name = Ui.Trim(ep.Name, 43);
                if (Safety.IsVoicemeeter(ep.Name))
                {
                    Console.WriteLine($"  {name,-44} пропущено: Voicemeeter");
                    continue;
                }
                IAudioClient? client = null;
                try
                {
                    client = Devices.ActivateAudioClient(ep.Id);
                    var mix = MixFormat(client);
                    var c3 = (IAudioClient3)client;
                    var (def, fund, min, max) = EnginePeriods(c3, mix);
                    uint cur = CurrentPeriod(c3);
                    string F(uint frames) => $"{frames} ({Ui.Num(frames * 1000.0 / mix.SampleRate, "F2")})";
                    Console.WriteLine($"  {name,-44} {Ui.Trim(mix.ToString(), 23),-24} {F(def),14} {fund,6} {F(min),14} {max,8} {F(cur),14}" +
                                      (min < def ? "  ← малые буферы есть" : ""));
                }
                catch (InvalidCastException)
                {
                    Console.WriteLine($"  {name,-44} IAudioClient3 нет");
                }
                catch (AudioException ex)
                {
                    Console.WriteLine($"  {name,-44} {ex.Message}");
                }
                finally
                {
                    if (client is not null) Marshal.ReleaseComObject(client);
                }
            }
        }
        Console.WriteLine();
        Console.WriteLine("  «сейчас» — текущий период движка на устройстве: если кто-то уже открыл поток с малым периодом, он меньше «по умолч.».");
        return 0;
    }

    private static (uint Default, uint Fundamental, uint Min, uint Max) EnginePeriods(IAudioClient3 c3, AudioFormat mix)
    {
        IntPtr p = mix.AllocCoTaskMem();
        try
        {
            AudioException.Check(c3.GetSharedModeEnginePeriod(p, out uint def, out uint fund, out uint min, out uint max), "GetSharedModeEnginePeriod");
            return (def, fund, min, max);
        }
        finally
        {
            Marshal.FreeCoTaskMem(p);
        }
    }

    private static uint CurrentPeriod(IAudioClient3 c3)
    {
        AudioException.Check(c3.GetCurrentSharedModeEnginePeriod(out IntPtr pf, out uint cur), "GetCurrentSharedModeEnginePeriod");
        Marshal.FreeCoTaskMem(pf);
        return cur;
    }

    private static LowLatResult RunLowLat(Endpoint dev, bool capture, double seconds, string periodOpt, double? level, bool lowLatency)
    {
        string mode = lowLatency ? "IAudioClient3" : "Initialize, буфер 10 мс";
        Ui.Title(mode);
        var client = Devices.ActivateAudioClient(dev.Id);
        var mix = MixFormat(client);
        var c3 = (IAudioClient3)client;
        var (def, fund, min, max) = EnginePeriods(c3, mix);
        uint period = 0;
        if (lowLatency)
        {
            uint want = periodOpt switch
            {
                "min" => min,
                "default" => def,
                _ => uint.TryParse(periodOpt, out uint v) ? v : throw new UsageException("--period: min, default или число кадров."),
            };
            period = fund == 0 ? want : Math.Clamp(min + (uint)Math.Round((Math.Max(want, min) - min) / (double)fund) * fund, min, max);
        }
        double Ms(uint frames) => frames * 1000.0 / mix.SampleRate;
        Console.WriteLine($"  формат {mix}; периоды движка: по умолчанию {def}, шаг {fund}, минимум {min} ({Ui.Num(Ms(min), "F2")} мс), максимум {max}; " +
                          $"сейчас {CurrentPeriod(c3)}");
        if (lowLatency) Console.WriteLine($"  прошу период {period} кадров ({Ui.Num(Ms(period), "F2")} мс)");

        int Init(IntPtr p) => lowLatency
            ? c3.InitializeSharedAudioStream(AudioConst.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, period, p, IntPtr.Zero)
            : client.Initialize(AudioConst.AUDCLNT_SHAREMODE_SHARED, AudioConst.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 100_000, 0, p, IntPtr.Zero);

        var glitchTimes = new List<long>();
        int timeouts;
        double mean, p99, maxI;
        uint buffer;
        string? error = null;
        CpuLoad load;
        uint running;
        long latency;
        if (!capture)
        {
            var (rs, hr) = RenderStream.TryCreateWith(mode, client, mix, Init, seconds + 10);
            if (rs is null) return Failed(mode, hr);
            if (level is { } lv)
            {
                double amp = Math.Pow(10, lv / 20), phase = 0, step = 2 * Math.PI * 1000 / mix.SampleRate;
                rs.Source = stereo =>
                {
                    for (int i = 0; i < stereo.Length; i += 2)
                    {
                        stereo[i] = stereo[i + 1] = (float)(amp * Math.Sin(phase));
                        phase += step;
                        if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
                    }
                };
            }
            rs.Start();
            (running, latency, load) = Measure(c3, client, seconds);
            rs.Stop();
            var iv = rs.Intervals();
            (mean, p99, maxI, timeouts, buffer) = (iv.Mean, iv.P99, iv.Max, rs.Timeouts, rs.BufferFrames);
            glitchTimes.AddRange(rs.UnderrunTimes);
            if (rs.ErrorHr != 0) error = $"поток оборвался: {Hr.Describe(rs.ErrorHr)}";
            Console.WriteLine($"  пробуждений {rs.Wakes}, записано кадров {rs.FramesWritten}, опустошений {rs.Underruns}");
            rs.Dispose();
        }
        else
        {
            var clock = Stopwatch.StartNew();
            var (cs, hr) = CaptureStream.TryCreateWith(mode, client, mix, Init, null, clock, seconds + 10);
            if (cs is null) return Failed(mode, hr);
            var times = new List<float>((int)(seconds * 1000) + 1000);
            long lastTicks = 0;
            // Позиция идёт в кадрах устройства, а пакет — в кадрах потока: при ресемплинге в движке
            // (микрофон 44,1 кГц, поток 48 кГц) они не совпадают. Сбой — приращение позиции заметно отличается
            // от обычного (первые пакеты задают обычное) или флаг DATA_DISCONTINUITY не на первом пакете.
            ulong prevPos = 0;
            long typical = 0;
            var firstPackets = new List<string>();
            int packets = 0, minPacket = int.MaxValue, maxPacket = 0;
            cs.OnPacket = (in PacketInfo p, ReadOnlySpan<float> _) =>
            {
                long now = Stopwatch.GetTimestamp();
                if (lastTicks != 0 && times.Count < times.Capacity) times.Add((float)((now - lastTicks) * 1000.0 / Stopwatch.Frequency));
                lastTicks = now;
                packets++;
                minPacket = Math.Min(minPacket, p.Frames);
                maxPacket = Math.Max(maxPacket, p.Frames);
                long delta = packets > 1 ? (long)(p.DevicePosition - prevPos) : 0;
                if (packets == 4) typical = delta;
                bool jump = typical > 0 && packets > 4 && Math.Abs(delta - typical) > typical / 4 + 1;
                if ((p.Discontinuity && packets > 1) || jump)
                    if (glitchTimes.Count < 100_000) glitchTimes.Add(now);
                if (firstPackets.Count < 8) firstPackets.Add($"{p.DevicePosition}+{p.Frames}{(p.Discontinuity ? "!D" : "")}{(p.Silent ? "!S" : "")}");
                prevPos = p.DevicePosition;
            };
            cs.Start();
            (running, latency, load) = Measure(c3, client, seconds);
            cs.Stop();
            Console.WriteLine($"  первые пакеты (позиция+кадров): {string.Join(" ", firstPackets)}");
            AudioException.Check(client.GetBufferSize(out buffer), "GetBufferSize");
            // интервалы между пакетами, а не пробуждениями: поток утилиты выбирает все пакеты за одно пробуждение
            var sorted = times.OrderBy(x => x).ToList();
            (mean, p99, maxI) = sorted.Count == 0 ? (0, 0, 0) : (sorted.Average(), sorted[(int)Math.Min(sorted.Count - 1, sorted.Count * 0.99)], sorted[^1]);
            timeouts = 0;
            if (cs.ErrorHr != 0) error = $"поток оборвался: {Hr.Describe(cs.ErrorHr)}";
            Console.WriteLine($"  пакетов {packets}, размер пакета {minPacket}…{maxPacket} кадров, сбоев {glitchTimes.Count}, кадров/с {Ui.Num(cs.Stats.Summarize(0.5, seconds).FramesPerSecond)}");
            cs.Dispose();
        }

        int worst = 0;
        for (int i = 0, j = 0; i < glitchTimes.Count; i++)
        {
            while (glitchTimes[i] - glitchTimes[j] > 60 * Stopwatch.Frequency) j++;
            worst = Math.Max(worst, i - j + 1);
        }
        Console.WriteLine($"  период движка во время прогона: {running} кадров ({Ui.Num(Ms(running), "F2")} мс), буфер {buffer} кадров, GetStreamLatency {Ui.Num(latency / 10_000.0, "F2")} мс");
        return new LowLatResult(mode, $"{running} ({Ui.Num(Ms(running), "F2")})", $"{buffer} ({Ui.Num(Ms(buffer), "F1")})", latency / 10_000.0,
            mean, p99, maxI, glitchTimes.Count, worst, timeouts, load.SelfCore, error);

        LowLatResult Failed(string m, int hr)
        {
            string e = $"Initialize: {Hr.Describe(hr)}";
            Ui.Warn(e);
            return new LowLatResult(m, "—", "—", double.NaN, 0, 0, 0, -1, int.MaxValue, 0, 0, e);
        }
    }

    /// <summary>Прогон: через 0,5 с спросить фактический период движка, затем ждать до конца и снять CPU.</summary>
    private static (uint Running, long Latency, CpuLoad Load) Measure(IAudioClient3 c3, IAudioClient client, double seconds)
    {
        var c0 = CpuSample.Take();
        Ui.Sleep(0.5);
        uint running = CurrentPeriod(c3);
        client.GetStreamLatency(out long latency);
        Ui.Sleep(seconds - 0.5);
        return (running, latency, CpuLoad.Between(c0, CpuSample.Take()));
    }
}
