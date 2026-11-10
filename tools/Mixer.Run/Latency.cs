using System.Diagnostics;
using Mixer.Audio.Engine;
using Mixer.Audio.Interop;

namespace Mixer.Run;

/// <summary>
/// Сквозная задержка «вход → движок → выход» по петле: выход физически (или кабелем VB-Cable) заведён обратно на вход,
/// в графе провод «вход → выход» с затуханием. В сигнал входа впрыскивается импульс; он обходит петлю, и интервал
/// между импульсом и его эхом в отсчётах входа — это ровно путь микрофон → выход вместе с буферами устройств и драйверов.
/// </summary>
public static class Latency
{
    public static int Run(Args cli, CancellationToken cancel)
    {
        var devices = DeviceCatalog.ListAll();
        var inEp = Args.FindDevice(devices, cli.Require("in"), EDataFlow.Capture);
        var outEp = Args.FindDevice(devices, cli.Require("out"), EDataFlow.Render);
        if ((inEp.IsVoicemeeter || outEp.IsVoicemeeter) && !cli.Has("allow-voicemeeter"))
            throw new UsageException("Voicemeeter VAIO без запущенного Voicemeeter вешал службу Windows Audio (этап 0). Если он запущен, добавьте --allow-voicemeeter.");

        int count = (int)cli.GetDouble("count", 5);
        float gainDb = (float)cli.GetDouble("gain", -6);
        float level = (float)Math.Pow(10, cli.GetDouble("level", -6) / 20);
        if (gainDb > -3) throw new UsageException("--gain выше −3 дБ опасен: петля может возбудиться");

        var options = Commands.OptionsFrom(cli);
        Console.WriteLine($"Петля: {outEp.Name} → {inEp.Name}; провод {gainDb} дБ, импульс {20 * Math.Log10(level):F0} dBFS, {count} замеров.");

        using var engine = new AudioEngine(options);
        engine.Apply(new EngineGraph(
            [new SourceSpec("in", SourceKind.DeviceInput, inEp.Id)],
            [new OutputSpec("out", outEp.Id)],
            [new WireSpec("in", "out", gainDb)]));

        var src = engine.FindSource("in")!;
        var dst = engine.FindOutput("out")!;
        var wire = engine.FindWire("in", "out")!;
        var sw = Stopwatch.StartNew();
        while (wire.State != WireState.Running && sw.Elapsed.TotalSeconds < 10 && !cancel.IsCancellationRequested)
        {
            if (src.State == NodeState.Error || dst.State == NodeState.Error)
                throw new UsageException($"Не удалось открыть устройства: {src.Message ?? dst.Message}");
            Thread.Sleep(50);
        }
        if (wire.State != WireState.Running) throw new UsageException("Провод не запустился за 10 с (нет пакетов от входа?)");
        Console.WriteLine($"Вход {src.Format}, выход {dst.Format}, буфер выхода {dst.BufferFrames} кадров. Прогрев 3 с…");
        cancel.WaitHandle.WaitOne(3000);

        int rate = src.Format!.SampleRate;
        var results = new List<double>();
        for (int k = 1; k <= count && !cancel.IsCancellationRequested; k++)
        {
            var ring = new SpscRing(rate * 3);
            src.AddTap(ring);
            cancel.WaitHandle.WaitOne(200);              // фон до импульса
            src.InjectImpulse(level);
            cancel.WaitHandle.WaitOne(1500);
            src.RemoveTap(ring);
            int n = ring.Available;
            var buf = new float[n * 2];
            ring.Read(buf, n);

            var echo = Analyze(buf, rate, level);
            if (echo is null)
            {
                Console.WriteLine($"  {k}: эхо не найдено — петля не замкнута или уровень слишком мал");
                continue;
            }
            var (ms, gain, next) = echo.Value;
            results.Add(ms);
            Console.WriteLine($"  {k}: {ms:F2} мс (эхо {gain:F1} дБ{(next is { } n2 ? $", следующий круг {n2:F2} мс" : "")})");
        }

        double est = src.CaptureLatencyMs + wire.LatencyMs + dst.RenderLatencyMs;
        Console.WriteLine();
        if (results.Count > 0)
        {
            results.Sort();
            Console.WriteLine($"Сквозная задержка по петле: медиана {results[results.Count / 2]:F2} мс, разброс {results[0]:F2}…{results[^1]:F2} мс");
        }
        Console.WriteLine($"Оценка по движку: захват ≈{src.CaptureLatencyMs:F1} + провод {wire.LatencyMs:F1} + выход ≈{dst.RenderLatencyMs:F1} = {est:F1} мс " +
                          "(без буферов драйверов и самих устройств)");
        Console.WriteLine($"Провод: кольцо {wire.FillMs:F2} мс, ε {wire.EpsilonPpm:+0.0;-0.0} ppm, underrun {wire.Underruns}, overrun {wire.Overruns}");
        return results.Count > 0 ? 0 : 1;
    }

    /// <summary>Импульс и его первое эхо в канале L. Возвращает интервал (мс), затухание эха (дБ) и интервал до второго эха.</summary>
    private static (double Ms, double GainDb, double? NextMs)? Analyze(float[] stereo, int rate, float level)
    {
        int n = stereo.Length / 2;
        float At(int i) => MathF.Abs(stereo[2 * i]);

        int p0 = -1;
        for (int i = 0; i < n; i++)
            if (At(i) >= level * 0.9f) { p0 = i; break; }
        if (p0 < 0) return null;

        float noise = 0;
        for (int i = 0; i < p0 - 10; i++) noise = MathF.Max(noise, At(i));

        int Peak(int from, int to)
        {
            int best = -1;
            float bestV = 0;
            for (int i = Math.Max(from, 0); i < Math.Min(to, n); i++)
                if (At(i) > bestV) { bestV = At(i); best = i; }
            return bestV > Math.Max(noise * 4, 1e-3f) ? best : -1;
        }

        int guard = rate * 3 / 1000; // 3 мс после импульса — его же «хвост»
        int p1 = Peak(p0 + guard, p0 + rate * 4 / 10);
        if (p1 < 0) return null;
        int interval = p1 - p0;
        int p2 = Peak(p1 + guard, p1 + interval + interval / 5);
        double gain = 20 * Math.Log10(At(p1) / level);
        return (interval * 1000.0 / rate, gain, p2 > 0 ? (p2 - p1) * 1000.0 / rate : null);
    }
}
