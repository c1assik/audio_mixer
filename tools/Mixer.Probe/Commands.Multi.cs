using System.Diagnostics;
using Mixer.Probe.Audio;
using Mixer.Probe.Interop;
using Mixer.Probe.Sys;

namespace Mixer.Probe;

internal static partial class Commands
{
    /// <summary>
    /// Поиск зависания при нескольких клиентах process loopback в одном процессе.
    /// Все вызовы COM трассируются; если вызов висит дольше --timeout, снимаются стеки и процесс завершается.
    /// </summary>
    public static int Multi(Args a)
    {
        int n = a.Int("clients", 2);
        double seconds = a.Dbl("seconds", 3);
        double timeout = a.Dbl("timeout", 8);
        bool mmcss = (a.Opt("mmcss") ?? "on") == "on";
        var priority = (a.Opt("priority") ?? "highest") == "normal" ? ThreadPriority.Normal : ThreadPriority.Highest;
        string stopMode = a.Opt("stop") ?? "each"; // each: поток и клиент по очереди; together: сначала все потоки, потом все клиенты; reverse: в обратном порядке
        bool overlap = (a.Opt("overlap") ?? "on") == "on";
        bool distinct = a.Flag("distinct");
        var format = Opts.Format(a);
        uint flags = Opts.ProcessFlags(a);

        var tones = new List<Process>();
        var log = new StepLog();
        var render = Devices.List(EDataFlow.Render);
        Console.WriteLine($"multi: клиентов {n}, {seconds} с, MMCSS {(mmcss ? "вкл" : "выкл")}, приоритет {priority}, остановка {stopMode}, " +
                          $"одновременно {(overlap ? "да" : "нет")}, цели {(distinct ? "разные" : "одна")}, {format}");

        log.StartWatchdog(timeout, stuck =>
        {
            log.Log("ЗАВИСАНИЕ: RESULT=HANG");
            foreach (var s in stuck) log.Log("  STUCK " + s);
            DumpStacks(log);
            foreach (var t in tones) TryKill(t);
            Console.Out.Flush();
            Environment.Exit(5);
        });

        try
        {
            List<uint> targets;
            if (a.Flag("tone"))
            {
                for (int i = 0; i < (distinct ? n : 1); i++)
                    tones.Add(log.Run($"запуск генератора тона {i}", () => StartTone(seconds * n + 20, a.Dbl("level", -70), "auto", render,
                        Path.Combine(Opts.OutDir(a), $"{Ui.Stamp}-multi-tone{i}.log"))));
                targets = tones.Select(t => (uint)t.Id).ToList();
                double settle = a.Dbl("settle", 0);
                if (settle > 0) log.Run($"пауза {settle} с после запуска тона", () => Thread.Sleep(TimeSpan.FromSeconds(settle)));
            }
            else
            {
                var roots = Opts.Roots(ProcessSnapshot.Take(), a.Req("exe"));
                if (distinct && roots.Count < n) throw new UsageException($"--distinct: нужно {n} корней, найдено {roots.Count}.");
                targets = roots.Select(r => r.Pid).ToList();
            }

            var clock = Stopwatch.StartNew();
            var done = new List<(string Name, Summary S, int Err)>();

            CaptureStream Create(int i)
            {
                uint pid = targets[distinct ? i : 0];
                var client = log.Run($"#{i}: активация process loopback, PID {pid}", () => ProcessLoopback.Activate(pid, excludeTree: false));
                var (s, hr) = log.Run($"#{i}: Initialize + GetService + SetEventHandle",
                    () => CaptureStream.TryCreate($"#{i}", client, format, flags, null, clock, seconds * n + 60));
                if (s is null) throw new AudioException($"#{i}: Initialize", hr);
                s.Trace = log;
                s.UseMmcss = mmcss;
                s.Priority = priority;
                return s;
            }

            void Collect(CaptureStream s, double from, double to) =>
                done.Add((s.Name, s.Stats.Summarize(from, to), s.ErrorHr));

            if (overlap)
            {
                var streams = Enumerable.Range(0, n).Select(Create).ToList();
                double t0 = clock.Elapsed.TotalSeconds;
                foreach (var s in streams) s.Start();
                Ui.Sleep(seconds);
                double t1 = clock.Elapsed.TotalSeconds;

                var order = stopMode == "reverse" ? Enumerable.Reverse(streams).ToList() : streams;
                if (stopMode == "together")
                {
                    foreach (var s in order) s.StopThread();
                    foreach (var s in order) s.StopClient();
                }
                else
                {
                    foreach (var s in order) s.Stop();
                }
                foreach (var s in streams) Collect(s, t0 + 0.3, t1);
                foreach (var s in order) s.Dispose();
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    var s = Create(i);
                    double t0 = clock.Elapsed.TotalSeconds;
                    s.Start();
                    Ui.Sleep(seconds);
                    double t1 = clock.Elapsed.TotalSeconds;
                    s.Stop();
                    Collect(s, t0 + 0.3, t1);
                    s.Dispose();
                }
            }

            log.StopWatchdog();
            Ui.Title("Итог multi");
            Ui.SummaryHeader();
            foreach (var d in done)
            {
                Ui.SummaryRow(d.Name, d.S, format.SampleRate);
                if (d.Err != 0) Ui.Warn($"{d.Name}: {Hr.Describe(d.Err)}");
            }
            foreach (var d in done) Console.WriteLine($"CLIENT {d.Name} frames={d.S.Frames}");
            Ui.Colored(ConsoleColor.Green, "OK: все клиенты запущены и остановлены без зависаний. RESULT=OK");
            return 0;
        }
        finally
        {
            log.StopWatchdog();
            foreach (var t in tones) TryKill(t);
        }
    }

    private static void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill();
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>Стеки всех потоков своего процесса через dotnet-stack (если установлен).</summary>
    private static void DumpStacks(StepLog log)
    {
        string tool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "dotnet-stack.exe");
        if (!File.Exists(tool))
        {
            log.Log("dotnet-stack не установлен, стеки не сняты");
            return;
        }
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add("report");
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000))
        {
            TryKill(p);
            log.Log("dotnet-stack не ответил за 30 с");
            return;
        }
        Console.WriteLine("──── стеки (dotnet-stack) ────");
        Console.WriteLine(stdout.Result);
        if (stderr.Result.Length > 0) Console.WriteLine(stderr.Result);
    }
}
