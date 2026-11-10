using System.Diagnostics;
using Mixer.Probe.Audio;
using Mixer.Probe.Interop;
using Mixer.Probe.Sys;

namespace Mixer.Probe;

internal static partial class Commands
{
    /// <summary>
    /// v2, этап 0, замер 2: сколько звука нового приложения теряет «Весь звук системы», пока наблюдатель не откроет захват.
    /// Генератор тона стартует в случайной фазе относительно тика. Отсчёт от IAudioClient.Start генератора до:
    /// обнаружения сессии (опрос раз в --tick с, как MixerHost в v1, или уведомление IAudioSessionNotification),
    /// открытия захвата и первого непустого пакета в нём.
    /// </summary>
    public static int NewApp(Args a)
    {
        int runs = a.Int("runs", 10);
        double tick = a.Dbl("tick", 1.0);
        double level = a.Dbl("level", -70);
        double toneSeconds = a.Dbl("tone-seconds", 4);
        string detect = a.Opt("detect") ?? "both";
        if (detect is not ("poll" or "notify" or "both")) throw new UsageException("--detect: poll, notify или both.");
        var format = Opts.Format(a);
        uint flags = Opts.ProcessFlags(a);
        var render = Devices.List(EDataFlow.Render);
        var device = Safety.Check(Devices.Find(render, a.Req("device")));
        var watched = render.Where(e => !Safety.IsVoicemeeter(e.Name)).ToList();
        string outDir = Opts.OutDir(a);
        string stamp = Ui.Stamp;

        Console.WriteLine($"newapp: {runs} запусков, обнаружение {detect}, тик {tick} с, генератор {Ui.Db(level)} dBFS в «{device.Name}», " +
                          $"сессии смотрю на {watched.Count} устройствах (Voicemeeter пропущен)");

        uint target = 0;
        string mode = "poll";
        long detectTicks = 0;
        var detected = new ManualResetEventSlim(false);
        void Detected(uint pid, long ticks, string by)
        {
            if (pid == 0 || pid != Volatile.Read(ref target) || mode != by || detected.IsSet) return;
            Interlocked.Exchange(ref detectTicks, ticks);
            detected.Set();
        }

        using var watcher = SessionWatcher.Subscribe(watched);
        foreach (string e in watcher.Errors) Ui.Warn("подписка: " + e);
        watcher.SessionCreated += (pid, t) => Detected(pid, t, "notify");

        // Наблюдатель, как в MixerHost: снимок процессов и сессий всех устройств раз в тик.
        var tickMs = new List<double>();
        var stopPoll = new ManualResetEventSlim(false);
        var poll = new Thread(() =>
        {
            while (!stopPoll.Wait(TimeSpan.FromSeconds(tick)))
            {
                long t0 = Stopwatch.GetTimestamp();
                ProcessSnapshot.Take();
                var sessions = Devices.Sessions(watched);
                long t1 = Stopwatch.GetTimestamp();
                lock (tickMs) tickMs.Add(Ms(t0, t1));
                uint want = Volatile.Read(ref target);
                if (want != 0 && sessions.Any(s => s.Pid == want && s.State != AudioConst.AudioSessionStateExpired)) Detected(want, t1, "poll");
            }
        })
        { IsBackground = true, Name = "poll" };
        poll.SetApartmentState(ApartmentState.MTA);
        poll.Start();

        var results = new List<(int Run, string Mode, double Detect, double Open, double Audio, string Note)>();
        var rnd = new Random();
        var clock = Stopwatch.StartNew();
        try
        {
            Ui.Title("Запуски");
            Console.WriteLine($"  {"№",3} {"способ",-7} {"сессия найдена, мс",19} {"захват открыт, мс",18} {"первый звук, мс",16}  примечание");
            for (int run = 1; run <= runs && !Ui.Cancel.IsSet; run++)
            {
                mode = detect == "both" ? (run % 2 == 1 ? "poll" : "notify") : detect;
                detected.Reset();
                Volatile.Write(ref target, 0);
                if (!Ui.Sleep(rnd.NextDouble() * tick + 0.2)) break;

                using var tone = ToneProcess.Start(new ToneOptions(toneSeconds, level, device.Id, LogPath: Path.Combine(outDir, $"{stamp}-newapp-{run}.log")));
                Volatile.Write(ref target, tone.Pid);
                string note = "";
                double dDetect = double.NaN, dOpen = double.NaN, dAudio = double.NaN;

                bool found = detected.Wait(TimeSpan.FromSeconds(tick + 5));
                bool started = tone.WaitStarted(TimeSpan.FromSeconds(5));
                if (!started)
                {
                    results.Add((run, mode, dDetect, dOpen, dAudio, "генератор не стартовал"));
                    Console.WriteLine($"  {run,3} {mode,-7} генератор не стартовал");
                    continue;
                }
                long start = tone.StartTicks!.Value;
                if (!found)
                {
                    note = "сессия не обнаружена";
                }
                else
                {
                    dDetect = Ms(start, Interlocked.Read(ref detectTicks));
                    ulong firstAudio = 0;
                    var audible = new ManualResetEventSlim(false);
                    var client = ProcessLoopback.Activate(tone.Pid, excludeTree: false);
                    var (s, hr) = CaptureStream.TryCreate($"newapp {run}", client, format, flags, null, clock, 60);
                    if (s is null) throw new AudioException("Initialize", hr);
                    s.OnPacket = (in PacketInfo p, ReadOnlySpan<float> x) =>
                    {
                        if (audible.IsSet || p.Silent) return;
                        double sum = 0;
                        foreach (float v in x) sum += (double)v * v;
                        if (sum / Math.Max(1, x.Length) > 1e-10) // −100 dBFS
                        {
                            firstAudio = p.Qpc100ns;
                            audible.Set();
                        }
                    };
                    s.Start();
                    dOpen = Ms(start, Stopwatch.GetTimestamp());
                    if (audible.Wait(TimeSpan.FromSeconds(3))) dAudio = Qpc.Ms(Qpc.To100ns(start), firstAudio);
                    else note = "в захвате тишина";
                    s.Dispose();
                }
                results.Add((run, mode, dDetect, dOpen, dAudio, note));
                Console.WriteLine($"  {run,3} {mode,-7} {Ui.Num(dDetect),19} {Ui.Num(dOpen),18} {Ui.Num(dAudio),16}  {note}");
            }
        }
        finally
        {
            stopPoll.Set();
            poll.Join(3000);
        }

        Ui.Title("Итог: сколько звука нового приложения теряется (до первого пакета в захвате)");
        foreach (var g in results.Where(r => !double.IsNaN(r.Audio)).GroupBy(r => r.Mode))
        {
            var v = g.Select(r => r.Audio).OrderBy(x => x).ToList();
            Console.WriteLine($"  {g.Key,-7} запусков {v.Count}: медиана {Ui.Num(v[v.Count / 2])} мс, мин {Ui.Num(v[0])}, макс {Ui.Num(v[^1])} мс");
        }
        int failed = results.Count(r => double.IsNaN(r.Audio));
        if (failed > 0) Ui.Warn($"Без результата: {failed} запусков.");
        lock (tickMs)
            if (tickMs.Count > 0)
                Console.WriteLine($"  тик наблюдателя (снимок процессов + сессии {watched.Count} устройств): ср {Ui.Num(tickMs.Average(), "F1")} мс, макс {Ui.Num(tickMs.Max(), "F1")} мс");
        Console.WriteLine("  Отрицательное «сессия найдена» — сессия появилась при Initialize, до Start генератора.");
        Console.WriteLine("RESULT=OK");
        return 0;
    }
}
