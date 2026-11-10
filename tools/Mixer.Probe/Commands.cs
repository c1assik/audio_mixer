using System.Diagnostics;
using System.Runtime.InteropServices;
using Mixer.Probe.Audio;
using Mixer.Probe.Interop;
using Mixer.Probe.Sys;

namespace Mixer.Probe;

internal static partial class Commands
{
    private static readonly ERole[] Roles = { ERole.Console, ERole.Multimedia, ERole.Communications };

    private const int E_INVALIDARG = unchecked((int)0x80070057);

    private const uint DeviceLoopbackFlags =AudioConst.AUDCLNT_STREAMFLAGS_LOOPBACK | AudioConst.AUDCLNT_STREAMFLAGS_EVENTCALLBACK;

    // ── list ─────────────────────────────────────────────────────────────

    public static int List()
    {
        var snap = ProcessSnapshot.Take();
        var render = Devices.List(EDataFlow.Render);
        var capture = Devices.List(EDataFlow.Capture);

        Ui.Title("Устройства воспроизведения (* — по умолчанию)");
        foreach (var e in render) Console.WriteLine($"  {(e.IsDefault ? "*" : " ")} {e.Name}\n      {e.Id}");
        Ui.Title("Устройства записи");
        foreach (var e in capture) Console.WriteLine($"  {(e.IsDefault ? "*" : " ")} {e.Name}\n      {e.Id}");

        Ui.Title("Аудиосессии воспроизведения");
        Ui.Sessions(Devices.Sessions(render), snap);

        Ui.Title("Системные API");
        try
        {
            var client = ProcessLoopback.Activate((uint)Environment.ProcessId, excludeTree: false);
            Marshal.ReleaseComObject(client);
            Ui.Check("process loopback: активация работает", true);
        }
        catch (AudioException ex)
        {
            Ui.Check("process loopback: активация работает", false, ex.Message);
        }
        try
        {
            using var policy = AudioPolicyConfig.Create();
            int hr = policy.GetPersistedDefaultAudioEndpoint((uint)Environment.ProcessId, EDataFlow.Render, ERole.Multimedia, out _);
            Ui.Check($"IAudioPolicyConfig: фабрика получена, вариант {policy.Variant}", true, $"пробный Get: {Hr.Describe(hr)}");
        }
        catch (AudioException ex)
        {
            Ui.Check("IAudioPolicyConfig", false, ex.Message);
        }
        return 0;
    }

    // ── capture ──────────────────────────────────────────────────────────

    public static int Capture(Args a)
    {
        double seconds = a.Dbl("seconds", 10);
        var format = Opts.Format(a);
        uint flags = Opts.ProcessFlags(a);
        string outDir = Opts.OutDir(a);
        string stamp = Ui.Stamp;
        var trace = a.Flag("trace") ? new StepLog() : null;

        var targets = new List<(string Label, uint Pid, bool Exclude)>();
        Endpoint? device = null;
        if (a.Opt("device") is { } deviceQuery)
        {
            device = Devices.Find(Devices.List(EDataFlow.Render), deviceQuery);
        }
        else if (a.Flag("system"))
        {
            targets.Add(("system", (uint)Environment.ProcessId, true));
        }
        else
        {
            var snap = ProcessSnapshot.Take();
            foreach (var r in Opts.Roots(snap, a.Req("exe")))
                targets.Add(($"{Path.GetFileNameWithoutExtension(r.Exe)}-{r.Pid}", r.Pid, false));
        }

        var clock = new Stopwatch();
        var streams = new List<CaptureStream>();
        double tEnd;
        try
        {
            foreach (var t in targets)
            {
                var client = ProcessLoopback.Activate(t.Pid, t.Exclude);
                var (s, hr) = CaptureStream.TryCreate(t.Label, client, format, flags,
                    Path.Combine(outDir, $"{stamp}-capture-{t.Label}.wav"), clock, seconds + 30);
                if (s is null) throw new AudioException($"IAudioClient.Initialize ({format})", hr);
                streams.Add(s);
            }
            if (device is not null)
            {
                var (s, hr) = CaptureStream.TryCreate($"loopback {device.Name}", Devices.ActivateAudioClient(device.Id), null, DeviceLoopbackFlags,
                    Path.Combine(outDir, $"{stamp}-capture-device.wav"), clock, seconds + 30);
                if (s is null) throw new AudioException("IAudioClient.Initialize (loopback устройства)", hr);
                streams.Add(s);
                format = s.Format;
            }

            Console.WriteLine($"Захват {string.Join(", ", streams.Select(s => s.Name))}: {format}, {seconds} с. Ctrl+C — остановить.");
            clock.Start();
            foreach (var s in streams)
            {
                s.Trace = trace;
                s.Start();
            }
            Ui.Sleep(seconds);
        }
        finally
        {
            tEnd = clock.Elapsed.TotalSeconds;
            foreach (var s in streams) s.Stop();
        }

        Ui.Title("Результат");
        Ui.SummaryHeader();
        foreach (var s in streams)
        {
            Ui.SummaryRow(s.Name, s.Stats.Summarize(0.5, tEnd), format.SampleRate);
            if (s.ErrorHr != 0) Ui.Warn($"{s.Name}: захват оборвался с {Hr.Describe(s.ErrorHr)}");
        }
        foreach (var s in streams)
        {
            // Устойчивая частота без стартового всплеска: от этого зависит запас PI-регулятора (ε ±1000 ppm в спецификации).
            if (tEnd < 6) break;
            var steady = s.Stats.Summarize(2, tEnd - 0.5);
            double ppm = (steady.FramesPerSecond / format.SampleRate - 1) * 1e6;
            Console.WriteLine($"  {s.Name}: устойчиво {Ui.Num(steady.FramesPerSecond, "F1")} кадров/с за {Ui.Num(steady.Seconds, "F1")} с, " +
                              $"отклонение от номинала {Ui.Num(ppm)} ppm (точность оценки ~±{Ui.Num(1e6 * 0.02 / steady.Seconds)} ppm)");
        }
        foreach (var s in streams) Console.WriteLine($"  WAV: {s.WavPath}");
        foreach (var s in streams) s.Dispose();
        return 0;
    }

    // ── formats ──────────────────────────────────────────────────────────

    public static int Formats(Args a)
    {
        string exe = a.Req("exe");
        double seconds = a.Dbl("seconds", 5);
        string outDir = Opts.OutDir(a);
        string stamp = Ui.Stamp;

        var snap = ProcessSnapshot.Take();
        var roots = Opts.Roots(snap, exe);
        var root = roots[0];
        if (roots.Count > 1) Ui.Warn($"Корней дерева несколько ({string.Join(", ", roots.Select(r => r.Pid))}), беру PID {root.Pid}.");

        const uint baseFlags = AudioConst.AUDCLNT_STREAMFLAGS_LOOPBACK | AudioConst.AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
        var modes = new (string Name, uint Flags)[]
        {
            ("auto", baseFlags | AudioConst.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM),
            ("auto+src", baseFlags | AudioConst.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AudioConst.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY),
            ("raw", baseFlags),
        };

        var trace = a.Flag("trace") ? new StepLog() : null;
        trace?.StartWatchdog(a.Dbl("timeout", 8), stuck =>
        {
            trace.Log("ЗАВИСАНИЕ: RESULT=HANG");
            foreach (var s in stuck) trace.Log("  STUCK " + s);
            DumpStacks(trace);
            Console.Out.Flush();
            Environment.Exit(5);
        });

        // Все варианты пишутся одновременно, чтобы уровни можно было сравнить на одном и том же звуке.
        var clock = new Stopwatch();
        var rows = new List<(string Label, AudioFormat Format, int Hr, CaptureStream? Stream)>();
        foreach (int rate in new[] { 48000, 44100 })
        foreach (bool isFloat in new[] { false, true })
        foreach (var mode in modes)
        {
            var fmt = AudioFormat.Simple(rate, 2, isFloat);
            string label = $"{(isFloat ? "float32" : "pcm16")} {rate} {mode.Name}";
            try
            {
                IAudioClient Activate() => ProcessLoopback.Activate(root.Pid, excludeTree: false);
                var client = trace is null ? Activate() : trace.Run($"{label}: активация", Activate);
                (CaptureStream?, int) Init() => CaptureStream.TryCreate(label, client, fmt, mode.Flags,
                    Path.Combine(outDir, $"{stamp}-formats-{label.Replace(' ', '-')}.wav"), clock, seconds + 30);
                var (s, hr) = trace is null ? Init() : trace.Run($"{label}: Initialize", Init);
                if (s is not null) s.Trace = trace;
                trace?.Log($"{label}: Initialize → {Hr.Describe(hr)}");
                rows.Add((label, fmt, hr, s));
            }
            catch (AudioException ex)
            {
                rows.Add((label, fmt, ex.HResult, null));
            }
        }

        Console.WriteLine($"Пишу {rows.Count(r => r.Stream is not null)} из {rows.Count} вариантов одновременно, {seconds} с. Приложение должно непрерывно играть.");
        double tEnd;
        clock.Start();
        try
        {
            foreach (var r in rows) r.Stream?.Start();
            Ui.Sleep(seconds);
        }
        finally
        {
            tEnd = clock.Elapsed.TotalSeconds;
            foreach (var r in rows) r.Stream?.Stop();
        }

        var reference = rows.FirstOrDefault(r => r.Label == "pcm16 48000 auto").Stream?.Stats.Summarize(0.3, tEnd);
        if (reference is null || reference.RmsDb < -50)
            Ui.Warn("Эталон pcm16 48000 почти молчит или не открылся — сравнение уровней недостоверно.");

        Ui.Title("Матрица форматов (эталон уровня — pcm16 48000 auto)");
        Console.WriteLine($"  {"вариант",-24}{"Initialize",-34}{"кадров/с",10}{"RMS",8}{"Δ RMS",8}{"пик",8}  оценка");
        foreach (var r in rows)
        {
            if (r.Stream is null)
            {
                Console.WriteLine($"  {r.Label,-24}{Ui.Trim(Hr.Describe(r.Hr), 33),-34}{"",10}{"",8}{"",8}{"",8}  не поддерживается");
                continue;
            }
            var s = r.Stream.Stats.Summarize(0.3, tEnd);
            double delta = reference is null ? double.NaN : s.RmsDb - reference.RmsDb;
            string verdict = Assess(r.Stream, s, r.Format.SampleRate, delta);
            Console.WriteLine($"  {r.Label,-24}{"S_OK",-34}{Ui.Num(s.FramesPerSecond),10}{Ui.Db(s.RmsDb),8}{Ui.Db(delta),8}{Ui.Db(s.PeakDb),8}  {verdict}");
        }

        Ui.Title("Выводы");
        bool floatOk = rows.Any(r => r.Format.IsFloat && r.Stream is not null &&
                                     Assess(r.Stream, r.Stream.Stats.Summarize(0.3, tEnd), r.Format.SampleRate, double.NaN) == "OK");
        bool rate44Ok = rows.Any(r => r.Format.SampleRate == 44100 && r.Stream is not null &&
                                      Assess(r.Stream, r.Stream.Stats.Summarize(0.3, tEnd), 44100, double.NaN) == "OK");
        Ui.Check("float32 работает в process loopback", floatOk);
        Ui.Check("захват сразу на 44100 Гц работает", rate44Ok);
        Console.WriteLine($"  WAV-файлы: {outDir}");
        foreach (var r in rows) r.Stream?.Dispose();
        trace?.StopWatchdog();
        Console.WriteLine("RESULT=OK");
        return 0;
    }

    private static string Assess(CaptureStream stream, Summary s, int rate, double deltaDb)
    {
        if (stream.ErrorHr != 0) return "оборвался: " + Hr.Describe(stream.ErrorHr);
        if (s.Frames == 0) return "нет данных";
        if (stream.Stats.NonFiniteSamples > 0) return "мусор: NaN/Inf";
        if (Math.Abs(s.FramesPerSecond - rate) / rate > 0.02) return $"частота {Ui.Num(s.FramesPerSecond)} ≠ {rate}";
        if (s.PeakDb > 6) return "мусор: пик > +6 dBFS";
        if (!double.IsNaN(deltaDb) && !double.IsInfinity(deltaDb) && Math.Abs(deltaDb) > 1.5) return "уровень расходится с эталоном";
        return "OK";
    }

    // ── test: ворота этапа 0 ─────────────────────────────────────────────

    public static int Test(Args a)
    {
        Process? tone = null;
        try
        {
            return TestCore(a, ref tone);
        }
        finally
        {
            if (tone is { HasExited: false })
            {
                tone.Kill();
                tone.WaitForExit(2000);
            }
        }
    }

    private static int TestCore(Args a, ref Process? tone)
    {
        bool useTone = a.Flag("tone");
        string exe = useTone ? Path.GetFileName(Environment.ProcessPath!) : a.Req("exe");
        string sinkQuery = a.Req("sink");
        double pre = a.Dbl("pre", 5), during = a.Dbl("during", 10), post = a.Dbl("post", 5);
        if (pre < 2 || during < 4 || post < 2) throw new UsageException("Минимальные длительности: --pre 2, --during 4, --post 2.");
        var format = Opts.Format(a);
        uint flags = Opts.ProcessFlags(a);
        string outDir = Opts.OutDir(a);
        string stamp = Ui.Stamp;
        var render = Devices.List(EDataFlow.Render);
        var sink = Devices.Find(render, sinkQuery);

        if (useTone)
        {
            string routing = a.Opt("routing") ?? "auto";
            tone = StartTone(pre + during + post + 8, a.Dbl("level", -30), routing, render, Path.Combine(outDir, $"{stamp}-tone.log"));
            Console.WriteLine($"  Генератор тона запущен: PID {tone.Id}, маршрутизация {routing}");
        }

        var snap = ProcessSnapshot.Take();
        uint? tonePid = tone is null ? null : (uint)tone.Id;
        List<ProcInfo> RootsOf(ProcessSnapshot s) =>
            tonePid is { } tp ? (s.ByPid.TryGetValue(tp, out var p) ? new List<ProcInfo> { p } : new List<ProcInfo>()) : Opts.Roots(s, exe);
        var roots = RootsOf(snap);
        if (roots.Count == 0) throw new UsageException("Генератор тона завершился раньше времени.");
        var tree = snap.Tree(roots.Select(r => r.Pid));
        var sessions = Devices.Sessions(render).Where(s => tree.Contains(s.Pid)).ToList();

        Ui.Title("Подготовка");
        Console.WriteLine($"  Приложение: {(useTone ? "генератор тона" : ProcessSnapshot.NormalizeExe(exe))}, корни: {string.Join(", ", roots.Select(r => r.Pid))}, процессов в дереве: {tree.Count}");
        Console.WriteLine($"  Заглушка:   {sink.Name}");
        Console.WriteLine($"  Захват:     {format}, флаги 0x{flags:X8}");
        Console.WriteLine("  Аудиосессии приложения:");
        Ui.Sessions(sessions, snap);

        var active = sessions.Where(s => s.IsActive).ToList();
        if (active.Count == 0)
            Ui.Warn("У приложения нет активной аудиосессии. Включите непрерывное воспроизведение (музыку или видео), иначе результат недостоверен.");
        var appEndpoint = active.Select(s => render.FirstOrDefault(e => e.Id == s.EndpointId)).FirstOrDefault(e => e is not null)
                          ?? render.FirstOrDefault(e => e.IsDefault)
                          ?? throw new UsageException("Нет устройства воспроизведения по умолчанию.");
        if (appEndpoint.Id == sink.Id)
            throw new UsageException($"Приложение уже играет в «{sink.Name}». Выберите другую заглушку или выполните reset --exe {exe}.");
        Console.WriteLine($"  Исходное устройство приложения: {appEndpoint.Name}");

        using var policy = AudioPolicyConfig.Create();
        Console.WriteLine($"  IAudioPolicyConfig: {policy.Variant}");
        var pids = sessions.Select(s => s.Pid).Concat(roots.Select(r => r.Pid)).Distinct().OrderBy(p => p).ToList();

        var clock = new Stopwatch();
        var procStreams = new List<CaptureStream>();
        CaptureStream? deviceStream = null;
        try
        {
            foreach (var r in roots)
            {
                var client = ProcessLoopback.Activate(r.Pid, excludeTree: false);
                var (s, hr) = CaptureStream.TryCreate($"process loopback {r.Pid}", client, format, flags,
                    Path.Combine(outDir, $"{stamp}-test-app-{r.Pid}.wav"), clock);
                if (s is null) throw new AudioException($"Initialize process loopback ({format})", hr);
                procStreams.Add(s);
            }
            var devClient = Devices.ActivateAudioClient(appEndpoint.Id);
            (deviceStream, int dhr) = CaptureStream.TryCreate($"loopback {appEndpoint.Name}", devClient, null, DeviceLoopbackFlags,
                Path.Combine(outDir, $"{stamp}-test-device.wav"), clock);
            if (deviceStream is null)
                Ui.Warn($"Loopback исходного устройства не открылся ({Hr.Describe(dhr)}), проверка двойного звука пропущена.");
        }
        catch
        {
            foreach (var s in procStreams) s.Dispose();
            throw;
        }

        var all = procStreams.Concat(deviceStream is null ? Array.Empty<CaptureStream>() : new[] { deviceStream }).ToList();
        string T() => $"{clock.Elapsed.TotalSeconds,5:F1} с";

        Ui.Title("Тест");
        bool redirected = false, cancelled = false, bindOk = false, resetOk = false;
        double tRedirect = double.NaN, tReset = double.NaN, tEnd;
        List<SessionInfo>? sessionsDuring = null;
        ProcessSnapshot? snapDuring = null;
        clock.Start();
        try
        {
            foreach (var s in all) s.Start();
            Console.WriteLine($"[{T()}] Фаза A, {pre} с: приложение играет как обычно.");
            if (!Ui.Sleep(pre))
            {
                cancelled = true;
            }
            else
            {
                tRedirect = clock.Elapsed.TotalSeconds;
                Console.WriteLine($"[{T()}] Фаза B, {during} с: перенаправляю в «{sink.Name}».");
                redirected = true;
                bindOk = ApplyBinding(policy, pids, sink.InterfacePath, snap);

                double look = Math.Min(2, during / 2);
                if (!Ui.Sleep(look))
                {
                    cancelled = true;
                }
                else
                {
                    snapDuring = ProcessSnapshot.Take();
                    var treeDuring = snapDuring.Tree(RootsOf(snapDuring).Select(r => r.Pid));
                    sessionsDuring = Devices.Sessions(render).Where(s => treeDuring.Contains(s.Pid)).ToList();
                    Console.WriteLine($"[{T()}] Аудиосессии приложения сейчас:");
                    Ui.Sessions(sessionsDuring, snapDuring);

                    if (!Ui.Sleep(during - look))
                    {
                        cancelled = true;
                    }
                    else
                    {
                        tReset = clock.Elapsed.TotalSeconds;
                        Console.WriteLine($"[{T()}] Фаза C, {post} с: сбрасываю привязку на «по умолчанию».");
                        resetOk = ApplyBinding(policy, pids, null, snap);
                        redirected = false;
                        if (!Ui.Sleep(post)) cancelled = true;
                    }
                }
            }
        }
        finally
        {
            if (redirected)
            {
                Console.WriteLine("Сбрасываю привязку перед выходом:");
                ApplyBinding(policy, pids, null, snap);
            }
            tEnd = clock.Elapsed.TotalSeconds;
            foreach (var s in all) s.Stop();
        }

        const double settle = 1.0;
        var phases = new List<(string Name, double From, double To)>();
        if (!double.IsNaN(tRedirect)) phases.Add(("A до перенаправления", 0.5, tRedirect));
        if (!double.IsNaN(tReset)) phases.Add(("B в заглушке", tRedirect + settle, tReset));
        if (!double.IsNaN(tReset)) phases.Add(("C после сброса", tReset + settle, tEnd));

        Ui.Title("Уровни по фазам (первая секунда после переключения отброшена)");
        Ui.SummaryHeader();
        foreach (var s in all)
        {
            Console.WriteLine($"  {s.Name} · {s.Format}");
            foreach (var p in phases)
                Ui.SummaryRow("  " + p.Name, s.Stats.Summarize(p.From, p.To), s == deviceStream ? 0 : format.SampleRate);
            if (s.ErrorHr != 0) Ui.Warn($"{s.Name}: захват оборвался с {Hr.Describe(s.ErrorHr)}");
        }

        if (cancelled)
        {
            Ui.Warn("Тест прерван, вывод не делается.");
            foreach (var s in all) s.Dispose();
            return 3;
        }

        var best = procStreams.OrderByDescending(s => s.Stats.Summarize(0.5, tRedirect).RmsDb).First();
        var pa = best.Stats.Summarize(0.5, tRedirect);
        var pb = best.Stats.Summarize(tRedirect + settle, tReset);

        bool appAudible = pa.RmsDb > -50;
        bool captureKept = best.ErrorHr == 0
                           && pb.FramesPerSecond >= 0.9 * format.SampleRate
                           && pb.RmsDb > -50
                           && pb.RmsDb >= pa.RmsDb - 6;
        bool sessionMoved = sessionsDuring?.Any(s => s.IsActive && s.EndpointId == sink.Id) == true;

        // Двойной звук по loopback исходного устройства. Мерить можно, только если в фазе A этот loopback слышит приложение:
        // у виртуальных устройств с собственной обработкой (Sonar и т. п.) loopback бывает почти пустым.
        bool? directGone = null, directBack = null;
        string? deviceNote = null;
        bool deviceHearsApp = false;
        if (deviceStream is not null)
        {
            var da = deviceStream.Stats.Summarize(0.5, tRedirect);
            var db = deviceStream.Stats.Summarize(tRedirect + settle, tReset);
            var dc = deviceStream.Stats.Summarize(tReset + settle, tEnd);
            deviceNote = $"RMS A {Ui.Db(da.RmsDb)} → B {Ui.Db(db.RmsDb)} → C {Ui.Db(dc.RmsDb)} dBFS";
            deviceHearsApp = da.RmsDb > -50 && da.RmsDb >= pa.RmsDb - 20;
            if (deviceHearsApp)
            {
                directGone = db.RmsDb < -60 || da.RmsDb - db.RmsDb >= 20;
                directBack = dc.RmsDb >= da.RmsDb - 6;
            }
            else if (da.Packets > 0 && db.Packets == 0)
            {
                // Loopback отдаёт пакеты, только пока в устройство кто-то играет. В фазе B их не было вовсе:
                // устройство простаивало, значит, приложение в него не играло.
                directGone = true;
                directBack = dc.Packets > 0;
                deviceNote += $"; пакетов A {da.Packets} → B 0 → C {dc.Packets}: в фазе B устройство простаивало";
            }
        }
        var sessionsOnSource = sessionsDuring?.Where(s => s.IsActive && s.EndpointId == appEndpoint.Id).ToList();
        bool leftSource = sessionsOnSource is { Count: 0 };

        Ui.Title("Итог");
        if (!appAudible) Ui.Warn($"В фазе A приложение почти не звучало (RMS {Ui.Db(pa.RmsDb)} dBFS): результат недостоверен.");
        Ui.Check("Привязка задана: Set и Get совпали", bindOk);
        Ui.Check("Активная сессия приложения появилась на заглушке", sessionMoved);
        Ui.Check("На исходном устройстве не осталось активной сессии приложения", leftSource);
        Ui.Check("Process loopback продолжил получать звук в фазе B", captureKept,
            $"RMS A {Ui.Db(pa.RmsDb)} → B {Ui.Db(pb.RmsDb)} dBFS, {Ui.Num(pb.FramesPerSecond)} кадров/с");
        var pc = best.Stats.Summarize(tReset + settle, tEnd);
        Ui.Check("Process loopback получает звук и после сброса (фаза C)", pc.RmsDb > -50 && pc.RmsDb >= pa.RmsDb - 3 && pc.FramesPerSecond >= 0.9 * format.SampleRate,
            $"RMS C {Ui.Db(pc.RmsDb)} dBFS, {Ui.Num(pc.FramesPerSecond)} кадров/с");
        if (directGone is not null)
        {
            Ui.Check("Звук ушёл из исходного устройства (по его loopback)", directGone.Value, deviceNote);
            Ui.Check("После сброса звук вернулся в исходное устройство (по его loopback)", directBack!.Value);
            Console.WriteLine("  Если на исходном устройстве в это время звучало что-то ещё, проверка по loopback может ошибиться.");
        }
        else if (deviceStream is not null)
        {
            Ui.Warn($"Loopback исходного устройства не слышит приложение даже в фазе A ({deviceNote}), двойной звук по нему не оценить. " +
                    "Опора — пункт про сессии выше.");
        }
        Ui.Check("Сброс привязки: Get вернул «по умолчанию»", resetOk);

        // Двойной звук считаем устранённым по loopback, а если он неприменим — по сессиям.
        bool doubleGone = directGone ?? (sessionMoved && leftSource);
        bool gate = appAudible && captureKept && doubleGone;
        Console.WriteLine();
        Ui.Colored(gate ? ConsoleColor.Green : ConsoleColor.Red,
            gate ? "ВОРОТА ЭТАПА 0 НА ЭТОЙ СБОРКЕ: ПРОЙДЕНЫ. «Забрать себе» работает через process loopback."
                 : "ВОРОТА ЭТАПА 0 НА ЭТОЙ СБОРКЕ: НЕ ПРОЙДЕНЫ, см. пункты выше.");
        foreach (var s in all) Console.WriteLine($"  WAV: {s.WavPath}");
        foreach (var s in all) s.Dispose();
        return gate ? 0 : 4;
    }

    /// <summary>Запускает дочерний «tone» и ждёт, пока у него появится активная аудиосессия.</summary>
    private static Process StartTone(double seconds, double levelDb, string routing, IReadOnlyList<Endpoint> render, string logPath)
    {
        var log = TextWriter.Synchronized(new StreamWriter(logPath, append: false) { AutoFlush = true });
        if (routing is not ("auto" or "fixed")) throw new UsageException("--routing: auto или fixed.");
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (string arg in new[] { "tone", "--seconds", Ui.Num(seconds, "F1"), "--level", Ui.Num(levelDb, "F1"), "--routing", routing })
            psi.ArgumentList.Add(arg);
        var p = Process.Start(psi) ?? throw new UsageException("Не удалось запустить генератор тона.");
        var started = Stopwatch.StartNew();
        void Log(string? line)
        {
            if (line is not null) log.WriteLine($"{started.Elapsed.TotalSeconds,6:F2} {line}");
        }
        p.OutputDataReceived += (_, e) => Log(e.Data);
        p.ErrorDataReceived += (_, e) => Log(e.Data);
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) => Log($"[процесс завершился, код {p.ExitCode}]");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 5)
        {
            if (p.HasExited) throw new UsageException($"Генератор тона завершился с кодом {p.ExitCode}.");
            if (Devices.Sessions(render).Any(s => s.Pid == p.Id && s.IsActive)) return p;
            Thread.Sleep(200);
        }
        Ui.Warn("Активная сессия генератора тона не появилась за 5 с, продолжаю.");
        return p;
    }

    public static int Tone(Args a)
    {
        TonePlayer.FrontOnly = (a.Opt("channels") ?? "all") == "front";
        TonePlayer.Device = a.Opt("device");
        TonePlayer.Noise = (a.Opt("signal") ?? "sine") switch
        {
            "sine" => false,
            "noise" => true,
            var x => throw new UsageException($"--signal: sine или noise, а не «{x}»."),
        };
        TonePlayer.WaitStdin = a.Flag("wait-stdin");
        return TonePlayer.Run(a.Dbl("seconds", 30), a.Dbl("level", -30), (a.Opt("routing") ?? "auto") switch
        {
            "auto" => true,
            "fixed" => false,
            _ => throw new UsageException("--routing: auto или fixed."),
        });
    }

    /// <summary>RMS по секундам и каналам для WAV, записанного утилитой.</summary>
    public static int WavStat(Args a)
    {
        string path = a.Req("file");
        double step = a.Dbl("step", 1);
        byte[] file = File.ReadAllBytes(path);
        AudioFormat? fmt = null;
        int dataOffset = -1, dataLength = 0;
        for (int pos = 12; pos + 8 <= file.Length;)
        {
            string id = System.Text.Encoding.ASCII.GetString(file, pos, 4);
            int size = BitConverter.ToInt32(file, pos + 4);
            if (id == "fmt ") fmt = AudioFormat.FromFmtChunk(file.AsSpan(pos + 8, size));
            if (id == "data")
            {
                dataOffset = pos + 8;
                dataLength = Math.Min(size, file.Length - dataOffset);
                break;
            }
            pos += 8 + size + (size & 1);
        }
        if (fmt is null || dataOffset < 0) throw new UsageException("Не WAV или нет чанков fmt/data.");

        int framesPerStep = (int)(fmt.SampleRate * step);
        int bytesPerStep = framesPerStep * fmt.BlockAlign;
        var samples = new float[framesPerStep * fmt.Channels];
        Console.WriteLine($"{Path.GetFileName(path)}: {fmt}, {Ui.Num(dataLength / (double)fmt.BlockAlign / fmt.SampleRate, "F1")} с");
        Console.WriteLine($"  {"от, с",6}  RMS по каналам, dBFS");
        for (int off = 0, n = 0; off + fmt.BlockAlign <= dataLength; off += bytesPerStep, n++)
        {
            int bytes = Math.Min(bytesPerStep, dataLength - off) / fmt.BlockAlign * fmt.BlockAlign;
            int count = bytes / fmt.BlockAlign * fmt.Channels;
            fmt.Decode(file.AsSpan(dataOffset + off, bytes), samples.AsSpan(0, count));
            var sum = new double[fmt.Channels];
            for (int i = 0; i < count; i++) sum[i % fmt.Channels] += (double)samples[i] * samples[i];
            int frames = count / fmt.Channels;
            var cols = sum.Select(s => Ui.Db(s > 0 ? 10 * Math.Log10(s / frames) : double.NegativeInfinity).PadLeft(7));
            Console.WriteLine($"  {Ui.Num(n * step, "F1"),6}  {string.Concat(cols)}");
        }
        return 0;
    }

    // ── redirect / reset / status ────────────────────────────────────────

    public static int Redirect(Args a)
    {
        string exe = a.Req("exe");
        var render = Devices.List(EDataFlow.Render);
        var sink = Devices.Find(render, a.Req("sink"));
        var snap = ProcessSnapshot.Take();
        var pids = BindingTargets(snap, exe, render);
        using var policy = AudioPolicyConfig.Create();
        Console.WriteLine($"IAudioPolicyConfig: {policy.Variant}. Перенаправляю {ProcessSnapshot.NormalizeExe(exe)} в «{sink.Name}»:");
        bool ok = ApplyBinding(policy, pids, sink.InterfacePath, snap);
        Ui.Warn($"Привязка сохраняется в Windows. Вернуть: Mixer.Probe reset --exe {exe}");
        return ok ? 0 : 4;
    }

    public static int Reset(Args a)
    {
        string exe = a.Req("exe");
        var snap = ProcessSnapshot.Take();
        if (snap.All(exe).Count == 0)
            throw new UsageException($"Процесс {ProcessSnapshot.NormalizeExe(exe)} не запущен, а API принимает только PID живого процесса.");
        var pids = BindingTargets(snap, exe, Devices.List(EDataFlow.Render));
        using var policy = AudioPolicyConfig.Create();
        Console.WriteLine($"Сбрасываю привязку {ProcessSnapshot.NormalizeExe(exe)} на «по умолчанию»:");
        return ApplyBinding(policy, pids, null, snap) ? 0 : 4;
    }

    public static int Status(Args a)
    {
        var render = Devices.List(EDataFlow.Render);
        var snap = ProcessSnapshot.Take();
        if (a.Flag("self"))
        {
            // Привязка хранится по пути exe, поэтому собственный процесс показывает привязку Mixer.Probe.exe,
            // и для этого не нужно открывать поток (а открытие потока на «плохой» заглушке может повесить Audiosrv).
            Devices.Sessions(render); // процесс должен «познакомиться» с аудиослужбой, иначе Get вернёт E_INVALIDARG
            using var self = AudioPolicyConfig.Create();
            uint me = (uint)Environment.ProcessId;
            foreach (var role in Roles)
            {
                int hr = self.GetPersistedDefaultAudioEndpoint(me, EDataFlow.Render, role, out string? path);
                Console.WriteLine($"    {role}: {(hr < 0 ? Hr.Describe(hr) : Devices.NameOf(render, path))}");
            }
            return 0;
        }

        string exe = a.Req("exe");
        var pids = BindingTargets(snap, exe, render);
        using var policy = AudioPolicyConfig.Create();
        Console.WriteLine($"IAudioPolicyConfig: {policy.Variant}");
        foreach (uint pid in pids)
        {
            var parts = Roles.Select(role =>
            {
                int hr = policy.GetPersistedDefaultAudioEndpoint(pid, EDataFlow.Render, role, out string? path);
                return hr < 0 ? $"{role}: {Hr.Describe(hr)}" : $"{role}: {Devices.NameOf(render, path)}";
            });
            Console.WriteLine($"    PID {pid,-6} {snap.ExeOf(pid),-24} {string.Join("; ", parts)}");
        }
        var tree = snap.Tree(snap.Roots(exe).Select(r => r.Pid));
        Console.WriteLine("  Аудиосессии:");
        Ui.Sessions(Devices.Sessions(render).Where(s => tree.Contains(s.Pid)), snap);
        return 0;
    }

    /// <summary>Корни дерева и процессы дерева, у которых есть аудиосессии.</summary>
    private static List<uint> BindingTargets(ProcessSnapshot snap, string exe, IReadOnlyList<Endpoint> render)
    {
        var roots = Opts.Roots(snap, exe);
        var tree = snap.Tree(roots.Select(r => r.Pid));
        return Devices.Sessions(render).Where(s => tree.Contains(s.Pid)).Select(s => s.Pid)
            .Concat(roots.Select(r => r.Pid)).Distinct().OrderBy(p => p).ToList();
    }

    /// <summary>Задаёт (path) или сбрасывает (null) привязку для всех ролей и сверяет её через Get.</summary>
    private static bool ApplyBinding(AudioPolicyConfig policy, IEnumerable<uint> pids, string? path, ProcessSnapshot snap)
    {
        string? wantId = Devices.EndpointIdFromInterfacePath(path);
        bool allOk = true;
        string? firstRaw = null;
        foreach (uint pid in pids)
        {
            var parts = new List<string>();
            foreach (var role in Roles)
            {
                int hrSet = policy.SetPersistedDefaultAudioEndpoint(pid, EDataFlow.Render, role, path);
                int hrGet = policy.GetPersistedDefaultAudioEndpoint(pid, EDataFlow.Render, role, out string? back);
                if (hrSet == E_INVALIDARG && hrGet == E_INVALIDARG)
                {
                    // Процесс ни разу не обращался к аудио: аудиослужба его не знает и привязку не принимает.
                    parts.Add($"{role}: пропущен (нет аудиоконтекста)");
                    continue;
                }
                firstRaw ??= back;
                string? gotId = Devices.EndpointIdFromInterfacePath(back);
                bool ok = hrSet >= 0 && hrGet >= 0 && string.Equals(gotId, wantId, StringComparison.OrdinalIgnoreCase);
                allOk &= ok;
                parts.Add(ok ? $"{role} ok" : $"{role}: Set {Hr.Describe(hrSet)}, Get {Hr.Describe(hrGet)} → {back ?? "пусто"}");
            }
            Console.WriteLine($"    PID {pid,-6} {Ui.Trim(snap.ExeOf(pid), 24),-24} {string.Join(", ", parts)}");
        }
        if (path is not null && firstRaw is not null) Console.WriteLine($"    Get вернул: {firstRaw}");
        return allOk;
    }
}
