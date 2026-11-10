using System.Diagnostics;
using Mixer.Probe.Audio;
using Mixer.Probe.Interop;
using Mixer.Probe.Sys;

namespace Mixer.Probe;

internal static partial class Commands
{
    private const double EnvelopeMs = 5;
    private const double SinkPresentDb = -100; // звук приложения в заглушке: выше этого уровня; без него там цифровые нули

    /// <summary>
    /// v2, этап 0, замер 3: разрыв при переключении «прямой маршрут ↔ захват».
    /// Прямой маршрут: приложение привязано (IAudioPolicyConfig) к выходу X и играет туда само.
    /// Захват: приложение привязано к заглушке, утилита берёт его process loopback и сама играет в X, как движок.
    /// Что слышно в X, видно по loopback X: огибающая RMS по 5 мс, отметки времени по QPC.
    /// Способы передачи (--modes): naive — переключить путь утилиты сразу вместе с привязкой;
    /// early — на пути «прямой → захват» включить путь утилиты за --lead мс до привязки;
    /// session — дождаться активной сессии приложения на новом устройстве; sink — дождаться, когда звук приложения
    /// появится в loopback заглушки (или исчезнет из него).
    /// </summary>
    public static int Direct(Args a)
    {
        bool useTone = a.Flag("tone");
        var render = Devices.List(EDataFlow.Render);
        var output = Safety.Check(Devices.Find(render, a.Req("output")));
        var sink = Safety.Check(Devices.Find(render, a.Req("sink")));
        if (output.Id == sink.Id) throw new UsageException("--output и --sink должны быть разными устройствами.");
        int cycles = a.Int("cycles", 3);
        double hold = a.Dbl("hold", 4);
        if (hold < 3) throw new UsageException("--hold не меньше 3 с: окно анализа после переключения — 3 с.");
        var modes = (a.Opt("modes") ?? "naive,early,session,sink").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (modes.Count == 0 || modes.Any(m => m is not ("naive" or "early" or "session" or "sink")))
            throw new UsageException("--modes: naive, early, session, sink через запятую.");
        double lead = a.Dbl("lead", 30);
        if (lead is < 0 or > 90) throw new UsageException("--lead: от 0 до 90 мс.");
        double level = a.Dbl("level", -70);
        bool noise = (a.Opt("signal") ?? "noise") == "noise";
        string outDir = Opts.OutDir(a);
        string stamp = Ui.Stamp;
        double total = hold * (1 + modes.Count * cycles * 2) + modes.Count * cycles * 2 * 3 + 20;

        using var policy = AudioPolicyConfig.Create();
        ToneProcess? tone = null;
        List<uint> pids = new(), roots = new();
        HashSet<uint> tree = new();
        var original = new Dictionary<uint, string?>();
        var cleanup = new List<IDisposable>();
        bool bound = false;

        try
        {
            Ui.Title("Подготовка");
            if (useTone)
            {
                tone = ToneProcess.Start(new ToneOptions(total + 30, level, Noise: noise, SeparateExe: true, WaitStdin: true,
                    LogPath: Path.Combine(outDir, $"{stamp}-direct-tone.log")));
                if (!tone.WaitReady(TimeSpan.FromSeconds(10))) throw new UsageException("Генератор не ответил READY за 10 с.");
                pids = roots = new List<uint> { tone.Pid };
                tree = new HashSet<uint> { tone.Pid };
                bound = true;
                if (!Bind(policy, pids, output.InterfacePath))
                {
                    // процесс без аудиоконтекста привязку не принял: он откроет поток на системном «по умолчанию»
                    Safety.CheckDefaultRender(render);
                    Ui.Warn("Привязка до старта не принята, задаю её после старта генератора.");
                }
                tone.Go();
                if (!tone.WaitStarted(TimeSpan.FromSeconds(5))) throw new UsageException("Генератор не начал играть за 5 с.");
                Bind(policy, pids, output.InterfacePath);
                Console.WriteLine($"  Приложение: генератор {(noise ? "шума" : "синуса")} {Ui.Db(level)} dBFS, {tone.ExeName}, PID {tone.Pid}");
            }
            else
            {
                string exe = a.Req("exe");
                var snap = ProcessSnapshot.Take();
                roots = Opts.Roots(snap, exe).Select(r => r.Pid).ToList();
                pids = BindingTargets(snap, exe, render);
                tree = snap.Tree(roots);
                foreach (uint pid in pids)
                    if (policy.GetPersistedDefaultAudioEndpoint(pid, EDataFlow.Render, ERole.Multimedia, out string? path) >= 0)
                        original[pid] = string.IsNullOrEmpty(path) ? null : path;
                Console.WriteLine($"  Приложение: {ProcessSnapshot.NormalizeExe(exe)}, корни {string.Join(", ", roots)}, привязываю PID {string.Join(", ", pids)}");
                Console.WriteLine($"  Исходная привязка: {string.Join("; ", original.Select(o => $"{o.Key} → {Devices.NameOf(render, o.Value)}"))}");
                bound = true;
                if (!Bind(policy, pids, output.InterfacePath)) Ui.Warn("Не все процессы приняли привязку к выходу.");
            }
            Console.WriteLine($"  Выход (прямой маршрут): {output.Name}");
            Console.WriteLine($"  Заглушка (путь через захват): {sink.Name}");
            if (WaitSession(tree, output, TimeSpan.FromSeconds(5)) is not { } onX)
                Ui.Warn("Активная сессия приложения на выходе не появилась за 5 с: если приложение не играет или не переезжает без перезапуска, результат недостоверен.");
            else
                Console.WriteLine($"  Сессия приложения на выходе через {Ui.Num(onX)} мс");

            // ── потоки ──
            var clock = Stopwatch.StartNew();
            var xClient = Devices.ActivateAudioClient(output.Id);
            var xMix = MixFormat(xClient);
            int rate = xMix.SampleRate;
            var (r, rhr) = RenderStream.TryCreate("выход", xClient, xMix, 200_000, total + 60);
            if (r is null) throw new AudioException("Initialize (выход)", rhr);
            cleanup.Add(r);
            r.QueueFrames = rate * 15 / 1000; // как RenderQueueMs движка

            var capFormat = AudioFormat.Simple(rate, 2, isFloat: true);
            var rings = new List<FloatRing>();
            var caps = new List<CaptureStream>();
            foreach (uint root in roots)
            {
                var ring = new FloatRing(rate);
                var (c, chr) = CaptureStream.TryCreate($"захват {root}", ProcessLoopback.Activate(root, excludeTree: false), capFormat,
                    Opts.ProcessFlags(a), null, clock, total + 60);
                if (c is null) throw new AudioException("Initialize (process loopback)", chr);
                c.OnPacket = (in PacketInfo p, ReadOnlySpan<float> x) => ring.Write(x);
                rings.Add(ring);
                caps.Add(c);
                cleanup.Add(c);
            }

            var (lx, lxhr) = CaptureStream.TryCreate("loopback выхода", Devices.ActivateAudioClient(output.Id), null, DeviceLoopbackFlags,
                Path.Combine(outDir, $"{stamp}-direct-output.wav"), clock, total + 60);
            if (lx is null) throw new AudioException("Initialize (loopback выхода)", lxhr);
            cleanup.Add(lx);
            var envX = new Envelope(lx.Format.SampleRate, lx.Format.Channels, EnvelopeMs, total + 60);
            lx.OnPacket = envX.Add;

            // Тишина в заглушку: loopback отдаёт пакеты, только пока в устройство кто-то играет.
            var sClient = Devices.ActivateAudioClient(sink.Id);
            var (rs, rshr) = RenderStream.TryCreate("заглушка", sClient, MixFormat(sClient), 200_000, total + 60);
            if (rs is null) throw new AudioException("Initialize (тишина в заглушку)", rshr);
            cleanup.Add(rs);
            var (ls, lshr) = CaptureStream.TryCreate("loopback заглушки", Devices.ActivateAudioClient(sink.Id), null, DeviceLoopbackFlags,
                Path.Combine(outDir, $"{stamp}-direct-sink.wav"), clock, total + 60);
            if (ls is null) throw new AudioException("Initialize (loopback заглушки)", lshr);
            cleanup.Add(ls);
            var envS = new Envelope(ls.Format.SampleRate, ls.Format.Channels, EnvelopeMs, total + 60);
            ls.OnPacket = envS.Add;

            // путь через захват: кольцо держит 20 мс свежего звука, как цель заполнения провода в движке
            bool forward = false;
            int keep = rate * 20 / 1000;
            long starved = 0;
            r.Source = stereo =>
            {
                bool on = Volatile.Read(ref forward);
                foreach (var ring in rings)
                {
                    if (!on)
                    {
                        ring.TrimTo(keep);
                        continue;
                    }
                    if (ring.AvailableFrames > keep + rate * 60 / 1000) ring.TrimTo(keep);
                    int got = ring.ReadAdd(stereo);
                    if (got < stereo.Length / 2) Interlocked.Add(ref starved, stereo.Length / 2 - got);
                }
            };

            lx.Start();
            ls.Start();
            rs.Start();
            foreach (var c in caps) c.Start();
            r.Start();
            Console.WriteLine($"  Выход {xMix}, захват {capFormat}, loopback выхода {lx.Format}, loopback заглушки {ls.Format}");

            // ── переключения ──
            var events = new List<SwitchEvent>();
            Ui.Title("Переключения");
            ulong refFrom = Qpc.Now100ns + 10_000_000;
            if (!Ui.Sleep(hold)) throw new OperationCanceledException();
            ulong refTo = Qpc.Now100ns;

            foreach (string mode in modes)
            for (int cycle = 1; cycle <= cycles; cycle++)
            {
                foreach (bool toCapture in new[] { true, false })
                {
                    if (Ui.Cancel.IsSet) break;
                    if (mode == "early" && toCapture)
                    {
                        // путь через захват запаздывает на кольцо и очередь выхода: включаем его заранее, наложение вместо разрыва
                        Volatile.Write(ref forward, true);
                        WaitUntil(() => false, TimeSpan.FromMilliseconds(lead));
                    }
                    ulong tSet = Qpc.Now100ns;
                    if (mode is "naive" or "early") Volatile.Write(ref forward, toCapture);
                    long b0 = Stopwatch.GetTimestamp();
                    bool ok = Bind(policy, pids, toCapture ? sink.InterfacePath : output.InterfacePath);
                    double setMs = Ms(b0, Stopwatch.GetTimestamp());
                    string note = ok ? "" : "привязка не принята";
                    bool handed = mode is "naive" or "early";
                    if (mode == "session")
                        handed = WaitSession(tree, toCapture ? sink : output, TimeSpan.FromSeconds(3)) is not null;
                    else if (mode == "sink")
                        handed = WaitUntil(() => toCapture ? envS.LatestDb > SinkPresentDb : envS.LatestDb <= SinkPresentDb, TimeSpan.FromSeconds(3));
                    if (!handed) note += (note.Length > 0 ? "; " : "") + "признак переезда не дождался, переключаю по таймауту";
                    Volatile.Write(ref forward, toCapture);
                    ulong tHand = mode == "early" && toCapture ? tSet - (ulong)(lead * 10_000) : Qpc.Now100ns;
                    events.Add(new SwitchEvent(mode, cycle, toCapture, tSet, tHand, setMs, note));
                    Console.WriteLine($"  {mode,-8} цикл {cycle}: {(toCapture ? "прямой → захват" : "захват → прямой")}, Set {Ui.Num(setMs)} мс, " +
                                      $"передача через {Ui.Num(tHand >= tSet ? Qpc.Ms(tSet, tHand) : -Qpc.Ms(tHand, tSet))} мс {note}");
                    if (!Ui.Sleep(hold)) break;
                }
            }
            Volatile.Write(ref forward, false);
            Ui.Sleep(0.5);

            foreach (var s in cleanup.OfType<CaptureStream>()) s.Stop();
            foreach (var s in cleanup.OfType<RenderStream>()) s.Stop();

            // ── анализ ──
            double refDb = envX.Median(refFrom, refTo);
            Ui.Title($"Разрывы и двойной звук в выходе (уровень прямого маршрута {Ui.Db(refDb)} dBFS; окна {EnvelopeMs} мс)");
            if (double.IsNaN(refDb) || refDb < -100)
                Ui.Warn("Loopback выхода почти не слышит приложение в прямом маршруте — разрывы по нему не оценить.");
            if (!noise || !useTone)
                Console.WriteLine("  Двойной звук надёжно виден только на шуме: две копии синуса или музыки складываются непредсказуемо.");
            Console.WriteLine($"  {"способ",-8} {"цикл",4} {"направление",-16} {"Set",5} {"переезд",8} {"передача",9} {"разрыв Σ",9} {"разрыв макс",12} {"двойной Σ",10}");
            var rows = new List<(SwitchEvent E, double Gap, double Longest, double Double, double Move)>();
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                ulong from = e.TSet - 1_000_000; // −100 мс
                ulong to = Math.Min(e.TSet + 30_000_000, i + 1 < events.Count ? events[i + 1].TSet - 500_000 : ulong.MaxValue);
                var w = envX.Windows.Where(x => x.T100ns >= from && x.T100ns < to).ToList();
                int gap = 0, run = 0, longest = 0, dbl = 0;
                foreach (var (_, db) in w)
                {
                    if (db < refDb - 6) { gap++; run++; longest = Math.Max(longest, run); }
                    else run = 0;
                    if (db > refDb + 2) dbl++;
                }
                double move = MoveMs(envS, e.TSet, to, e.ToCapture);
                rows.Add((e, gap * EnvelopeMs, longest * EnvelopeMs, dbl * EnvelopeMs, move));
                Console.WriteLine($"  {e.Mode,-8} {e.Cycle,4} {(e.ToCapture ? "прямой → захват" : "захват → прямой"),-16} {Ui.Num(e.SetMs),5} {Ui.Num(move),8} " +
                                  $"{Ui.Num(e.THand >= e.TSet ? Qpc.Ms(e.TSet, e.THand) : -Qpc.Ms(e.THand, e.TSet)),9} {Ui.Num(gap * EnvelopeMs),9} {Ui.Num(longest * EnvelopeMs),12} {Ui.Num(dbl * EnvelopeMs),10}");
            }

            Ui.Title("Итог по способам (мс, худший цикл)");
            foreach (var g in rows.GroupBy(x => (x.E.Mode, x.E.ToCapture)))
                Console.WriteLine($"  {g.Key.Mode,-8} {(g.Key.ToCapture ? "прямой → захват" : "захват → прямой"),-16} разрыв макс {Ui.Num(g.Max(x => x.Longest))}, " +
                                  $"разрыв Σ {Ui.Num(g.Max(x => x.Gap))}, двойной Σ {Ui.Num(g.Max(x => x.Double))}, переезд потока {Ui.Num(g.Max(x => x.Move))}");
            Console.WriteLine($"  Путь через захват: опустошений кольца {Interlocked.Read(ref starved)} кадров, опустошений буфера выхода {r.Underruns}.");
            Console.WriteLine("  «переезд» — когда звук приложения появился в заглушке (прямой → захват) или исчез из неё (захват → прямой), от Set.");
            Console.WriteLine($"  WAV: {lx.WavPath}");
            Console.WriteLine($"  WAV: {ls.WavPath}");
            Console.WriteLine("RESULT=OK");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Ui.Warn("Прервано.");
            return 3;
        }
        finally
        {
            if (bound)
            {
                Console.WriteLine("Возвращаю исходную привязку приложения.");
                bool restored = tone is not null
                    ? Bind(policy, new List<uint> { tone.Pid }, null)
                    : pids.Select(pid => Bind(policy, new List<uint> { pid }, original.GetValueOrDefault(pid))).ToList().Any(ok => ok);
                if (!restored)
                    Ui.Warn($"Привязку вернуть не удалось (процесс завершился?). Она хранится в Windows по exe: " +
                            $"Mixer.Probe reset --exe {(tone is not null ? ToneProcess.SeparateExeName : a.Opt("exe"))}, пока процесс запущен.");
            }
            foreach (var d in Enumerable.Reverse(cleanup)) d.Dispose();
            tone?.Dispose();
        }
    }

    private sealed record SwitchEvent(string Mode, int Cycle, bool ToCapture, ulong TSet, ulong THand, double SetMs, string Note);

    /// <summary>Привязка всех ролей без печати. true — хотя бы один процесс её принял, и все принявшие вернули её через Get.</summary>
    private static bool Bind(AudioPolicyConfig policy, IReadOnlyList<uint> pids, string? path)
    {
        string? want = Devices.EndpointIdFromInterfacePath(path);
        bool any = false, all = true;
        foreach (uint pid in pids)
        {
            foreach (var role in Roles)
            {
                int hrSet = policy.SetPersistedDefaultAudioEndpoint(pid, EDataFlow.Render, role, path);
                if (hrSet == E_INVALIDARG) continue; // процесс без аудиоконтекста
                int hrGet = policy.GetPersistedDefaultAudioEndpoint(pid, EDataFlow.Render, role, out string? back);
                bool ok = hrSet >= 0 && hrGet >= 0 &&
                          string.Equals(Devices.EndpointIdFromInterfacePath(string.IsNullOrEmpty(back) ? null : back), want, StringComparison.OrdinalIgnoreCase);
                any |= ok;
                all &= ok;
            }
        }
        return any && all;
    }

    /// <summary>Ждёт активную сессию процесса из дерева на устройстве; возвращает мс ожидания или null по таймауту.</summary>
    private static double? WaitSession(HashSet<uint> tree, Endpoint device, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        var one = new[] { device };
        while (sw.Elapsed < timeout && !Ui.Cancel.IsSet)
        {
            if (Devices.Sessions(one).Any(s => s.IsActive && tree.Contains(s.Pid))) return sw.Elapsed.TotalMilliseconds;
            Thread.Sleep(10);
        }
        return null;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !Ui.Cancel.IsSet)
        {
            if (condition()) return true;
            Thread.Sleep(1);
        }
        return false;
    }

    /// <summary>Через сколько мс после Set звук приложения появился в заглушке (или исчез из неё на 4 окна подряд).</summary>
    private static double MoveMs(Envelope env, ulong from, ulong to, bool appear)
    {
        int run = 0;
        foreach (var (t, db) in env.Windows)
        {
            if (t < from || t >= to) continue;
            bool present = db > SinkPresentDb;
            if (appear && present) return Qpc.Ms(from, t);
            if (!appear)
            {
                run = present ? 0 : run + 1;
                if (run == 4) return Qpc.Ms(from, t) - 3 * EnvelopeMs;
            }
        }
        return double.NaN;
    }

    private static AudioFormat MixFormat(IAudioClient client)
    {
        AudioException.Check(client.GetMixFormat(out IntPtr p), "GetMixFormat");
        try
        {
            return AudioFormat.FromPointer(p);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p);
        }
    }
}
