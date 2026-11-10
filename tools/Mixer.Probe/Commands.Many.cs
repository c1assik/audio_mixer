using System.Diagnostics;
using Mixer.Probe.Audio;
using Mixer.Probe.Interop;
using Mixer.Probe.Sys;

namespace Mixer.Probe;

internal static partial class Commands
{
    /// <summary>
    /// v2, этап 0, замер 1: много одновременных include-захватов в одном процессе, как у «Весь звук системы».
    /// Запускает N генераторов тона (каждый — отдельный процесс, отдельная цель захвата) и открывает захваты ступенями.
    /// На каждой ступени: CPU утилиты, audiodg и системы, память, время открытия, сбои поступления звука.
    /// </summary>
    public static int Many(Args a)
    {
        int max = a.Int("clients", 24);
        var steps = (a.Opt("steps") ?? DefaultSteps(max)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out int v) && v >= 0 && v <= max ? v : throw new UsageException($"--steps: «{x}» не от 0 до {max}."))
            .Distinct().OrderBy(v => v).ToList();
        double seconds = a.Dbl("seconds", 20);
        double level = a.Dbl("level", -70);
        bool detail = a.Flag("detail");
        var format = Opts.Format(a);
        uint flags = Opts.ProcessFlags(a);
        var render = Devices.List(EDataFlow.Render);
        var device = Safety.Check(Devices.Find(render, a.Req("device")));
        string outDir = Opts.OutDir(a);
        string stamp = Ui.Stamp;

        Console.WriteLine($"many: до {max} захватов, ступени {string.Join(", ", steps)}, по {seconds} с; генераторы {Ui.Db(level)} dBFS в «{device.Name}»; " +
                          $"захват {format}, логических процессоров {Environment.ProcessorCount}");

        var log = new StepLog { Quiet = true };
        var tones = new List<ToneProcess>();
        var streams = new List<CaptureStream>();
        var openMs = new List<(double Activate, double Init, double Start)>();
        log.StartWatchdog(a.Dbl("timeout", 8), stuck =>
        {
            log.Log("ЗАВИСАНИЕ: RESULT=HANG");
            foreach (var s in stuck) log.Log("  STUCK " + s);
            DumpStacks(log);
            foreach (var t in tones) t.Dispose();
            Console.Out.Flush();
            Environment.Exit(5);
        });

        double total = steps.Count * (seconds + 3) + 30;
        var rows = new List<string>();
        try
        {
            Ui.Title($"Запуск {max} генераторов");
            for (int i = 0; i < max; i++)
                tones.Add(ToneProcess.Start(new ToneOptions(total, level, device.Id, LogPath: Path.Combine(outDir, $"{stamp}-many-tone{i}.log"))));
            var sw = Stopwatch.StartNew();
            int active = 0;
            while (sw.Elapsed.TotalSeconds < 15 && !Ui.Cancel.IsSet)
            {
                var pids = Devices.Sessions(new[] { device }).Where(s => s.IsActive).Select(s => s.Pid).ToHashSet();
                active = tones.Count(t => pids.Contains(t.Pid));
                if (active == max) break;
                if (tones.FirstOrDefault(t => t.HasExited) is { } dead) throw new UsageException($"Генератор PID {dead.Pid} завершился, см. логи в {outDir}.");
                Thread.Sleep(250);
            }
            Console.WriteLine($"  активных сессий генераторов: {active} из {max} за {Ui.Num(sw.Elapsed.TotalSeconds, "F1")} с");
            if (active < max) Ui.Warn("Не все генераторы заиграли, захваты на них будут молчать.");

            var clock = Stopwatch.StartNew();
            Ui.Title("Ступени");
            Console.WriteLine($"  {"захв.",5} {"откр. мс ср/макс",17} {"утилита %ядра",14} {"audiodg %ядра",14} {"система %CPU",13} {"частн. МБ",10} {"audiodg МБ",11} " +
                              $"{"разрывы",8} {"провалы",8} {"не та частота",14} {"RMS разброс",12}");
            foreach (int k in steps)
            {
                int before = streams.Count;
                while (streams.Count < k)
                {
                    int i = streams.Count;
                    uint pid = tones[i].Pid;
                    var t0 = Stopwatch.GetTimestamp();
                    var client = log.Run($"#{i}: активация", () => ProcessLoopback.Activate(pid, excludeTree: false));
                    var t1 = Stopwatch.GetTimestamp();
                    var (s, hr) = log.Run($"#{i}: Initialize", () => CaptureStream.TryCreate($"#{i}", client, format, flags, null, clock, total + 60));
                    if (s is null) throw new AudioException($"#{i}: Initialize", hr);
                    var t2 = Stopwatch.GetTimestamp();
                    log.Run($"#{i}: Start", s.Start);
                    var t3 = Stopwatch.GetTimestamp();
                    streams.Add(s);
                    openMs.Add((Ms(t0, t1), Ms(t1, t2), Ms(t2, t3)));
                }
                var stepOpen = openMs.Skip(before).Select(o => o.Activate + o.Init + o.Start).ToList();

                if (!Ui.Sleep(1)) break; // прогрев после открытия
                var c0 = CpuSample.Take();
                double w0 = clock.Elapsed.TotalSeconds;
                if (!Ui.Sleep(seconds)) break;
                double w1 = clock.Elapsed.TotalSeconds;
                var c1 = CpuSample.Take();
                var load = CpuLoad.Between(c0, c1);

                int discont = 0, starved = 0, badRate = 0;
                var rms = new List<double>();
                long minFrames = (long)(format.SampleRate * LevelStats.BucketSeconds / 2);
                foreach (var s in streams)
                {
                    var sum = s.Stats.Summarize(w0, w1);
                    discont += sum.Discontinuities;
                    starved += s.Stats.StarvedBuckets(w0, w1, minFrames);
                    if (Math.Abs(sum.FramesPerSecond - format.SampleRate) / format.SampleRate > 0.02 || s.ErrorHr != 0) badRate++;
                    rms.Add(sum.RmsDb);
                }
                string spread = rms.Count == 0 ? "—" : $"{Ui.Db(rms.Min())}…{Ui.Db(rms.Max())}";
                string open = stepOpen.Count == 0 ? "—" : $"{Ui.Num(stepOpen.Average())}/{Ui.Num(stepOpen.Max())}";
                string row = $"  {k,5} {open,17} {Ui.Num(load.SelfCore, "F2"),14} {Opt(load.AudioDgCore),14} {Ui.Num(load.SystemTotal, "F1"),13} " +
                             $"{Ui.Num(c1.SelfPrivateBytes / 1048576.0, "F1"),10} {Opt(c1.AudioDgWorkingSet / 1048576.0),11} " +
                             $"{discont,8} {starved,8} {badRate,14} {spread,12}";
                Console.WriteLine(row);
                rows.Add(row);
                var firsts = streams.Skip(before).Select(s => s.FirstPacketMs).ToList();
                if (firsts.Count > 0)
                    Console.WriteLine($"        первый пакет у новых захватов через, мс: {string.Join(" ", firsts.Select(f => double.IsNaN(f) ? "нет" : Ui.Num(f)))}");
                foreach (var s in streams)
                {
                    var sum = s.Stats.Summarize(w0, w1);
                    int st = s.Stats.StarvedBuckets(w0, w1, minFrames);
                    bool odd = st > 0 || sum.Discontinuities > 0 || Math.Abs(sum.RmsDb - level + 3) > 1.5 || s.ErrorHr != 0;
                    if (!odd && !detail) continue;
                    // по секундам: кадров и RMS, чтобы видеть, провал ли это поступления или тишина в самом генераторе
                    var perSecond = Enumerable.Range(0, (int)(w1 - w0))
                        .Select(i => s.Stats.Summarize(w0 + i, w0 + i + 1))
                        .Select(x => $"{x.Frames / 1000}k/{Ui.Db(x.RmsDb)}");
                    Console.WriteLine($"        {s.Name,-4} {Ui.Num(sum.FramesPerSecond),6} кадров/с, провалов {st}, RMS {Ui.Db(sum.RmsDb)}, тихих пакетов {sum.SilentPackets}: " +
                                      string.Join(" ", perSecond));
                }
            }

            Ui.Title("Остановка");
            var stopMs = new List<double>();
            foreach (var s in streams)
            {
                var t0 = Stopwatch.GetTimestamp();
                log.Run($"{s.Name}: Stop", s.Stop);
                log.Run($"{s.Name}: Dispose", s.Dispose);
                stopMs.Add(Ms(t0, Stopwatch.GetTimestamp()));
            }
            streams.Clear();
            log.StopWatchdog();
            if (stopMs.Count > 0) Console.WriteLine($"  остановлено {stopMs.Count} захватов, на каждый мс: ср {Ui.Num(stopMs.Average())}, макс {Ui.Num(stopMs.Max())}");
            if (openMs.Count > 0)
                Console.WriteLine($"  открытие, мс: активация ср {Ui.Num(openMs.Average(o => o.Activate))} / макс {Ui.Num(openMs.Max(o => o.Activate))}, " +
                                  $"Initialize ср {Ui.Num(openMs.Average(o => o.Init))} / макс {Ui.Num(openMs.Max(o => o.Init))}, " +
                                  $"Start ср {Ui.Num(openMs.Average(o => o.Start), "F1")}");
            Console.WriteLine("  «разрывы» — пакеты с DATA_DISCONTINUITY; «провалы» — корзины 100 мс, где пришло меньше половины кадров;");
            Console.WriteLine("  «RMS разброс» — каждый захват должен слышать только свой генератор (уровни одинаковые).");
            Console.WriteLine("RESULT=OK");
            return 0;
        }
        finally
        {
            log.StopWatchdog();
            foreach (var s in streams) s.Dispose();
            foreach (var t in tones) t.Dispose();
        }
    }

    private static string DefaultSteps(int max) =>
        string.Join(",", new[] { 0, 1, 6, 12, 18, 24, max }.Where(v => v <= max).Distinct());

    private static double Ms(long fromTicks, long toTicks) => (toTicks - fromTicks) * 1000.0 / Stopwatch.Frequency;

    private static string Opt(double? v) => v is { } x ? Ui.Num(x, "F2") : "н/д";
}
