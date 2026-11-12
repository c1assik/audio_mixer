using System.Diagnostics;
using System.Globalization;
using Mixer.Audio.Dsp;
using Mixer.Audio.Engine;
using Mixer.Audio.Interop;
using Mixer.Core.Apps;
using Mixer.Core.Config;
using Mixer.Core.Graph;
using Mixer.Core.Host;

namespace Mixer.Run;

public static class Commands
{
    public static int Help()
    {
        Console.WriteLine("""
        Mixer.Run — микшер без интерфейса (этапы 1–2). Граф задаётся JSON-файлом.

          devices [--formats]                 устройства (с --formats — ещё и формат микса)
          apps                                приложения: сначала с аудиосессией, ниже — остальные с окном
          sink-check --sink <имя>             проверить устройство как заглушку «Забрать себе» (пробный поток 2 с)
          example <graph.json> [--in <имя> | --no-mic] [--app <exe>] [--system] [--out <имя>] [--out2 <имя>]
                               [--sink <имя>] [--no-takeover]
                                              записать пример графа на устройствах этой машины
          run <graph.json> [--seconds N] [--report S] [--csv файл] [--watch] [--envelope <узел> [--envelope-csv файл]]
                           [--resampler sinc|cubic] [--render-ms M] [--queue-ms Q] [--target-periods P] [--max-ppm E] [--max-drift-ppm D]
                           [--allow-feedback] [--allow-voicemeeter] [--no-takeover]
                                              запустить граф; Ctrl+C — остановить (привязки приложений снимаются)
          latency --in <вход> --out <выход> [--count N] [--gain дБ] [--render-ms M] [--low-latency | --low-latency-out]
                                              сквозная задержка по петле «выход → кабель → вход»;
                                              --low-latency — IAudioClient3 на входе и выходе, --low-latency-out — только на выходе

        Устройства указываются по ID или по части имени.
        run пишет журнал привязок (boundApps) обратно в graph.json до каждой установки привязки.
        Код выхода run: 0 — ни одного underrun/overrun/потерь, 1 — были сбои. Паузы приложений сбоем не считаются.
        """);
        return 0;
    }

    public static int Devices(Args cli)
    {
        bool formats = cli.Has("formats");
        foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
        {
            Console.WriteLine(flow == EDataFlow.Render ? "── Воспроизведение (* — по умолчанию) ──" : "── Запись ──");
            foreach (var d in DeviceCatalog.List(flow).OrderBy(d => d.Name))
            {
                string fmt = "";
                if (formats)
                {
                    // эндпоинты Voicemeeter не трогаем: без запущенного Voicemeeter они вешали службу (этап 0)
                    fmt = d.IsVoicemeeter ? "  (Voicemeeter — пропущено)" : "  " + (DeviceCatalog.TryGetMixFormat(d.Id)?.ToString() ?? "формат недоступен");
                }
                string tag = FeedbackRules.IsVirtualCable(d.Name) ? " [кабель]" : d.IsVoicemeeter ? " [VAIO]" : "";
                Console.WriteLine($"  {(d.IsDefault ? '*' : ' ')} {d.Name}{tag}{fmt}");
                Console.WriteLine($"      {d.Id}");
            }
        }
        return 0;
    }

    public static int Example(Args cli)
    {
        string path = cli.Positional(0) ?? throw new UsageException("Укажите путь: example <graph.json>");
        var devices = DeviceCatalog.ListAll();
        EndpointInfo In(string? q) => q is null
            ? devices.FirstOrDefault(d => d.Flow == EDataFlow.Capture && d.IsDefault) ?? throw new UsageException("Нет устройства записи по умолчанию")
            : Args.FindDevice(devices, q, EDataFlow.Capture);
        EndpointInfo Out(string? q) => q is null
            ? devices.FirstOrDefault(d => d.Flow == EDataFlow.Render && d.IsDefault) ?? throw new UsageException("Нет устройства воспроизведения по умолчанию")
            : Args.FindDevice(devices, q, EDataFlow.Render);

        var nodes = new List<NodeConfig>();
        double y = 60;
        if (!cli.Has("no-mic"))
        {
            var mic = In(cli.Get("in"));
            nodes.Add(new() { Id = "mic", Kind = NodeKind.DeviceInput, Name = "Микрофон", EndpointId = mic.Id, EndpointName = mic.Name, X = 40, Y = y, Color = "#4FC3F7" });
            y += 140;
        }
        if (cli.Get("app") is { } exe)
        {
            string norm = Mixer.Core.Sys.ProcessSnapshot.NormalizeExe(exe);
            nodes.Add(new() { Id = "app", Kind = NodeKind.App, Name = Path.GetFileNameWithoutExtension(norm), Exe = norm, TakeOver = !cli.Has("no-takeover"), X = 40, Y = y, Color = "#BA68C8" });
            y += 140;
        }
        if (cli.Has("system"))
            nodes.Add(new() { Id = "system", Kind = NodeKind.System, Name = "Весь звук системы", X = 40, Y = y, Color = "#FFB74D" });
        if (nodes.Count == 0) throw new UsageException("Нет ни одного источника: уберите --no-mic или добавьте --app / --system");

        var sources = nodes.Select(n => n.Id).ToList();
        var wires = new List<WireConfig>();
        int i = 1;
        foreach (string? q in new[] { cli.Get("out"), cli.Get("out2") })
        {
            if (i > 1 && q is null) break;
            var o = Out(q);
            string id = $"out{i}";
            nodes.Add(new NodeConfig { Id = id, Kind = NodeKind.DeviceOutput, Name = o.Name, EndpointId = o.Id, EndpointName = o.Name, X = 620, Y = 60 + 140 * (i - 1) });
            wires.AddRange(sources.Select(s => new WireConfig { From = s, To = id }));
            i++;
        }
        string? sink = cli.Get("sink") is { } sq ? Args.FindDevice(devices, sq, EDataFlow.Render).Id : null;
        GraphSerializer.Save(path, new GraphConfig { SinkEndpoint = sink, Nodes = nodes, Wires = wires });
        Console.WriteLine($"Записан {Path.GetFullPath(path)}");
        return 0;
    }

    public static int Apps(Args cli)
    {
        var render = DeviceCatalog.List(EDataFlow.Render);
        var names = render.ToDictionary(d => d.Id, d => d.Name);
        var procs = Mixer.Core.Sys.ProcessSnapshot.Take();
        var sessions = AudioSessions.List(render.Select(d => d.Id));
        Console.WriteLine("── С аудиосессией ──");
        var withAudio = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in sessions.Where(s => s.Pid != 0).GroupBy(s => procs.ExeOf(s.Pid), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key))
        {
            withAudio.Add(g.Key);
            var roots = procs.Roots(g.Key).Select(r => r.Pid);
            string where = string.Join(", ", g.Select(s => $"{names.GetValueOrDefault(s.EndpointId, "?")} ({(s.IsActive ? "активна" : s.State == AudioSessionState.Inactive ? "неактивна" : "истекла")})").Distinct());
            Console.WriteLine($"  {g.Key,-28} корни {string.Join(",", roots),-14} {where}");
        }
        Console.WriteLine("── Остальные с окном ──");
        foreach (var p in System.Diagnostics.Process.GetProcesses().OrderBy(p => p.ProcessName))
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero || p.Id == Environment.ProcessId) continue;
                string exe = Mixer.Core.Sys.ProcessSnapshot.NormalizeExe(p.ProcessName);
                if (!withAudio.Add(exe)) continue;
                Console.WriteLine($"  {exe,-28} «{p.MainWindowTitle}»");
            }
            catch (InvalidOperationException) { /* процесс завершился */ }
            finally { p.Dispose(); }
        }
        return 0;
    }

    public static int SinkCheck(Args cli)
    {
        var devices = DeviceCatalog.ListAll();
        var ep = Args.FindDevice(devices, cli.Require("sink"), EDataFlow.Render);
        Console.WriteLine($"Проверка «{ep.Name}» как заглушки (пробный поток, таймаут {SinkValidator.ProbeTimeout.TotalSeconds:F0} с)…");
        var r = SinkValidator.Check(ep.Id, devices, Mixer.Core.Sys.ProcessSnapshot.Take(), id => StreamProbe.TryRender(id, SinkValidator.ProbeTimeout), MixerHost.MixChannels);
        Console.WriteLine(r.Verdict switch
        {
            SinkVerdict.Ok => "  годится",
            SinkVerdict.Warning => "  годится, но: " + r.Message,
            _ => "  НЕ ГОДИТСЯ: " + r.Message,
        });
        return r.Usable ? 0 : 1;
    }

    public static EngineOptions OptionsFrom(Args cli) => new()
    {
        Resampler = cli.Get("resampler")?.ToLowerInvariant() switch
        {
            null or "sinc" => ResamplerKind.Sinc,
            "cubic" => ResamplerKind.Cubic,
            var v => throw new UsageException($"--resampler: «{v}» — нужно sinc или cubic"),
        },
        RenderBufferMs = cli.GetDouble("render-ms", 10),
        RenderQueueMs = cli.GetDouble("queue-ms", 15),
        TargetPeriods = cli.GetDouble("target-periods", 2.0),
        MaxPpm = cli.GetDouble("max-ppm", 1000),
        MaxDriftPpm = cli.GetDouble("max-drift-ppm", 20000),
    };

    public static int Run(Args cli, CancellationToken cancel)
    {
        string path = cli.Positional(0) ?? throw new UsageException("Укажите граф: run <graph.json>");
        double seconds = cli.GetDouble("seconds", double.PositiveInfinity);
        double report = cli.GetDouble("report", 5);
        var graph = GraphSerializer.Load(path);
        var devices = DeviceCatalog.ListAll();

        var vaio = graph.Nodes.Where(n => n.IsOutput).Select(n => GraphBuilder.Resolve(n, devices)).Where(ep => ep is { IsVoicemeeter: true }).ToList();
        if (vaio.Count > 0 && !cli.Has("allow-voicemeeter"))
            throw new UsageException("В графе есть выход на Voicemeeter VAIO. Без запущенного Voicemeeter такой поток вешал службу Windows Audio (этап 0). " +
                                     "Если Voicemeeter запущен, добавьте --allow-voicemeeter.");
        if (!graph.Nodes.Any(n => n.IsSource) || !graph.Nodes.Any(n => n.IsOutput))
            throw new UsageException("Нечего запускать: в графе нет ни одного источника или выхода.");

        var options = OptionsFrom(cli);
        Console.WriteLine($"Ресемплер {options.Resampler}, буфер выхода {options.RenderBufferMs} мс (очередь {options.RenderQueueMs} мс), цель кольца {options.TargetPeriods} периода. Ctrl+C — стоп.");

        using var csv = cli.Get("csv") is { } csvPath ? new StreamWriter(csvPath) { AutoFlush = true } : null;
        csv?.WriteLine("time_s;wire;fill_ms;eps_ppm;latency_ms;underruns;overruns;resyncs;dropped;out_starvations;src_breaks;cpu_pct;gaps");

        var sw = Stopwatch.StartNew();
        var host = new MixerHost(graph, g => GraphSerializer.Save(path, g), options,
            new MixerHostOptions
            {
                AllowFeedback = cli.Has("allow-feedback"),
                DisableTakeOver = cli.Has("no-takeover"),
            });
        host.Log += m => Console.WriteLine($"[{sw.Elapsed.TotalSeconds,6:F1} с] {m}");
        int exit;
        try
        {
            host.Start();
            host.WaitReady(TimeSpan.FromSeconds(30));
            var st = host.Status;
            foreach (var issue in st.Build?.Issues ?? [])
                Console.WriteLine($"  ! {issue.Subject}: {issue.Message}");

            string? tracePath = cli.Get("trace");
            if (tracePath is not null)
                foreach (var w in host.Engine.Wires) w.Trace = new WireTrace(200_000);
            var monitor = new StatusMonitor(host);
            double next = report;
            // --watch: правка файла на ходу применяется как правка из интерфейса (проверка переключений на устройствах)
            bool watch = cli.Has("watch");
            var stamp = File.GetLastWriteTimeUtc(path);
            // --envelope <узел>: огибающая этого источника (loopback выхода) и разрывы вокруг каждой правки графа
            string? envNode = cli.Get("envelope");
            EnvelopeRecorder? env = null;
            while (!cancel.IsCancellationRequested && sw.Elapsed.TotalSeconds < seconds)
            {
                cancel.WaitHandle.WaitOne(watch ? 50 : 200);
                if (envNode is not null && env is null && host.Engine.FindSource(envNode) is { Format: not null } envSrc)
                {
                    env = new EnvelopeRecorder(envSrc, sw);
                    Console.WriteLine($"[{sw.Elapsed.TotalSeconds,6:F1} с] огибающая «{envNode}» пишется");
                }
                if (watch && File.GetLastWriteTimeUtc(path) != stamp)
                {
                    stamp = File.GetLastWriteTimeUtc(path);
                    try
                    {
                        var edited = GraphSerializer.Load(path);
                        if (GraphChange.Classify(host.Graph, edited) is not (GraphChangeKind.None or GraphChangeKind.Layout))
                        {
                            Console.WriteLine($"[{sw.Elapsed.TotalSeconds,6:F1} с] граф перечитан из файла");
                            env?.Mark(DescribeEdit(host.Graph, edited));
                            host.Update(edited);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
                    {
                        // файл пишется — следующая попытка через 50 мс
                        stamp = default;
                    }
                }
                double t = sw.Elapsed.TotalSeconds;
                if (t < next && t < seconds && !cancel.IsCancellationRequested) continue;
                next += report;
                monitor.Print(t, csv);
            }

            if (tracePath is not null) TraceDump.Save(tracePath, host.Engine);
            env?.Report(cli.Get("envelope-csv"));

            Console.WriteLine();
            Console.WriteLine($"── Итог за {sw.Elapsed.TotalSeconds:F0} с ──");
            exit = monitor.PrintSummary() ? 0 : 1;
        }
        finally
        {
            Console.WriteLine("Остановка…");
            host.Dispose();
        }
        return exit;
    }

    /// <summary>Что поменялось в правке (для отметки на огибающей): провода и усиления узлов.</summary>
    private static string DescribeEdit(GraphConfig a, GraphConfig b)
    {
        var parts = new List<string>();
        foreach (var w in b.Wires.Where(w => !a.Wires.Any(x => x.From == w.From && x.To == w.To))) parts.Add($"+провод {w.From}→{w.To}");
        foreach (var w in a.Wires.Where(w => !b.Wires.Any(x => x.From == w.From && x.To == w.To))) parts.Add($"−провод {w.From}→{w.To}");
        foreach (var n in b.Nodes)
            if (a.Nodes.FirstOrDefault(x => x.Id == n.Id) is { } old && old.GainDb != n.GainDb) parts.Add($"{n.Id}: {old.GainDb:+0.#;−0.#;0} → {n.GainDb:+0.#;−0.#;0} дБ");
        return parts.Count == 0 ? "правка" : string.Join(", ", parts);
    }
}

/// <summary>Сводка состояния движка раз в несколько секунд: уровни, заполнение колец, ε, сбои, CPU, аллокации.</summary>
public sealed class StatusMonitor(MixerHost host)
{
    private readonly AudioEngine engine = host.Engine;
    private readonly Process _proc = Process.GetCurrentProcess();
    private TimeSpan _cpu = Process.GetCurrentProcess().TotalProcessorTime;
    private long _allocated = GC.GetTotalAllocatedBytes();
    private int _gen0 = GC.CollectionCount(0), _gen1 = GC.CollectionCount(1), _gen2 = GC.CollectionCount(2);
    private double _lastT;
    private readonly List<double> _cpuSamples = [];
    private double _minLatency = double.MaxValue, _maxLatency;
    private readonly Dictionary<string, (long Frames, double T)> _first = new();
    private double _now;

    /// <summary>
    /// Средняя фактическая частота узла с первого ненулевого замера (кадров в секунду по QPC).
    /// За короткий интервал она шумит на ±0,5% из-за пакетов по 10 мс, за минуты точна до единиц ppm.
    /// </summary>
    private string Rate(string key, long frames, int nominal)
    {
        if (frames == 0) return "—";
        if (!_first.TryGetValue(key, out var f0))
        {
            _first[key] = (frames, _now);
            return "—";
        }
        double span = _now - f0.T;
        if (span < 1) return "—";
        double rate = (frames - f0.Frames) / span;
        return nominal > 0 ? $"{rate:F1} к/с ({(rate / nominal - 1) * 1e6:+0;-0} ppm)" : $"{rate:F1} к/с";
    }

    private string Name(string id)
    {
        string nodeId = GraphBuilder.NodeIdOf(id);
        string name = host.Graph.Nodes.FirstOrDefault(n => n.Id == nodeId)?.Name ?? nodeId;
        return id.Length > nodeId.Length ? $"{name} [{id[(nodeId.Length + 1)..]}]" : name;
    }

    public static string Dbfs(float v) => v <= 0 ? "  −∞" : $"{20 * Math.Log10(v),5:F1}";

    public void Print(double t, StreamWriter? csv)
    {
        _now = t;
        _proc.Refresh();
        var cpuNow = _proc.TotalProcessorTime;
        double dt = Math.Max(t - _lastT, 1e-3);
        double cpuPct = (cpuNow - _cpu).TotalSeconds / dt / Environment.ProcessorCount * 100;
        long alloc = GC.GetTotalAllocatedBytes();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        Console.WriteLine($"[{t,6:F0} с] CPU {cpuPct:F2}% ({Environment.ProcessorCount} лог. ядер), память {_proc.PrivateMemorySize64 / 1048576.0:F1} МБ, " +
                          $"аллокации {(alloc - _allocated) / 1024.0:F0} КБ, GC {g0 - _gen0}/{g1 - _gen1}/{g2 - _gen2}");
        if (t > 15) _cpuSamples.Add(cpuPct);
        _cpu = cpuNow;
        _allocated = alloc;
        (_gen0, _gen1, _gen2) = (g0, g1, g2);
        _lastT = t;

        foreach (var s in engine.Sources)
        {
            var (l, r) = s.Peak.Take();
            Console.WriteLine($"  ▶ {Name(s.Id),-24} {State(s)} {s.Format}  пик {Dbfs(l)} / {Dbfs(r)} dBFS" +
                              $"  пакетов {s.Packets}, средняя {Rate("s:" + s.Id, s.FramesCaptured, s.Format?.SampleRate ?? 0)}, разрывов {s.Discontinuities}, захват ≈{s.CaptureLatencyMs:F1} мс" +
                              (s.IsProcessLoopback ? $", без пакетов {s.SecondsSinceActivity:F1} с" : "") + LowLatencyText(s));
        }
        var st = host.Status;
        foreach (var (exe, b) in st.Bindings)
            Console.WriteLine($"  ⇄ {exe,-24} {MixerHost.Describe(b)}");
        if (st.BoundApps.Count > 0)
            Console.WriteLine($"  журнал привязок: {string.Join(", ", st.BoundApps)}");
        foreach (var (id, route) in st.Build?.DirectRoutes ?? new Dictionary<string, DirectRoute>())
        {
            var (l, r) = st.DirectMeters.TryGetValue(id, out var m) ? m.Take() : (0f, 0f);
            Console.WriteLine($"  ⚡ {Name(id),-24} напрямую в «{Name(route.OutputId)}», {route.GainDb:F1} дБ{(route.Muted ? ", mute" : "")}, " +
                              $"сессий {st.DirectSessions.GetValueOrDefault(id)}, пик сессии {Dbfs(l)} / {Dbfs(r)} dBFS");
        }
        if (st.VolumeApps.Count > 0)
            Console.WriteLine($"  журнал громкостей: {string.Join(", ", st.VolumeApps)}");
        foreach (var (exe, b) in st.MicBindings)
            Console.WriteLine($"  🎙 {exe,-24} микрофон: {(b.State == Mixer.Core.Apps.BindingState.Bound ? "назначен" : MixerHost.Describe(b))}");
        if (st.MicApps.Count > 0)
            Console.WriteLine($"  журнал микрофонов: {string.Join(", ", st.MicApps)}");
        foreach (var (id, warnings) in st.CableWarnings)
            foreach (string w in warnings)
                Console.WriteLine($"  ⚠ {Name(id),-24} {w}");
        foreach (var (id, plan) in st.Build?.SystemPlans ?? new Dictionary<string, SystemPlan>())
            if (plan.Skipped.Count > 0)
                Console.WriteLine($"  ⊘ {Name(id),-24} пропущено: {string.Join(", ", plan.Skipped.Select(s => s.Reason == SkipReason.ContainsExcluded ? $"{s.Exe} ({s.Reason}: {s.Because})" : $"{s.Exe} ({s.Reason})"))}");
        foreach (var o in engine.Outputs)
        {
            var (l, r) = o.Peak.Take();
            float lim = o.TakeLimiterMinGain();
            Console.WriteLine($"  ◀ {Name(o.Id),-24} {State(o)} {o.Format}  пик {Dbfs(l)} / {Dbfs(r)} dBFS  буфер {o.QueueFrames}/{o.BufferFrames} кадров ≈{o.RenderLatencyMs:F1} мс, средняя {Rate("o:" + o.Id, o.FramesRendered, o.Format?.SampleRate ?? 0)}" +
                              $"  пусто при пробуждении {o.Starvations}, нет события {o.EventTimeouts}, ограничитель {(lim < 1 ? $"{Db.FromGain(lim):F1} дБ" : "—")}" + LowLatencyText(o));
        }
        foreach (var w in engine.Wires)
        {
            var src = engine.FindSource(w.FromId);
            var dst = engine.FindOutput(w.ToId);
            double total = (src?.CaptureLatencyMs ?? 0) + w.LatencyMs + (dst?.RenderLatencyMs ?? 0);
            if (w.State == WireState.Running && t > 15)
            {
                _minLatency = Math.Min(_minLatency, total);
                _maxLatency = Math.Max(_maxLatency, total);
            }
            var (l, r) = w.Peak.Take();
            Console.WriteLine($"    {Name(w.FromId)} → {Name(w.ToId)}: {w.State}, {w.InputRate / 1000.0:0.#}→{w.OutputRate / 1000.0:0.#} кГц, " +
                              $"кольцо {w.FillMs:F2}/{w.TargetMs:F0} мс (сейчас {w.RawFillMs:F1}), ε {w.EpsilonPpm,7:+0.0;-0.0} ppm{(w.Tracking ? " слежение" : " захват")}, дрейф {(double.IsNaN(w.DriftPpm) ? "—" : w.DriftPpm.ToString("+0;-0"))} ppm, провод {w.LatencyMs:F1} мс, итог ≈{total:F1} мс, " +
                              $"пик {Dbfs(l)}, underrun {w.Underruns}, overrun {w.Overruns}, сбросов {w.Resyncs}, потеряно {w.DroppedFrames}" +
                              (src?.IsProcessLoopback == true ? $", пауз {w.Gaps}" : ""));
            csv?.WriteLine(string.Join(';', t.ToString("F1", CultureInfo.InvariantCulture), $"{w.FromId}->{w.ToId}",
                w.FillMs.ToString("F3", CultureInfo.InvariantCulture), w.EpsilonPpm.ToString("F1", CultureInfo.InvariantCulture),
                total.ToString("F2", CultureInfo.InvariantCulture), w.Underruns, w.Overruns, w.Resyncs, w.DroppedFrames, dst?.Starvations ?? 0, src?.Discontinuities ?? 0,
                cpuPct.ToString("F2", CultureInfo.InvariantCulture), w.Gaps));
        }
    }

    /// <summary>Режим «Низкая задержка» узла (пусто, если не просили).</summary>
    private static string LowLatencyText(AudioNode n) => !n.LowLatencyRequested ? "" : n.LowLatency.Status switch
    {
        LowLatencyStatus.Active => $", малый период {n.LowLatency.PeriodFrames} кадров ({n.LowLatency.PeriodMs:F2} мс), сбоев с ним {(n is OutputNode o ? o.Starvations : ((SourceNode)n).Discontinuities)}",
        LowLatencyStatus.Unsupported => $", малых периодов нет (минимум {n.LowLatency.MinFrames} кадров)",
        LowLatencyStatus.Unavailable => $", IAudioClient3 не включился: {n.LowLatency.Error}",
        LowLatencyStatus.FellBack => $", ОТКАТ с малого периода: больше {GlitchWindow.Limit} сбоев за минуту",
        _ => "",
    };

    private static string State(AudioNode n) => n.State switch
    {
        NodeState.Running => "работает",
        NodeState.Starting => "запуск…",
        NodeState.WaitingDevice => $"ждёт ({n.Message})",
        NodeState.Error => $"ОШИБКА: {n.Message}",
        _ => "остановлен",
    };

    /// <summary>Итог; true — ни одного сбоя.</summary>
    public bool PrintSummary()
    {
        bool clean = true;
        foreach (var w in engine.Wires)
        {
            bool ok = w.Underruns == 0 && w.Overruns == 0 && w.Resyncs == 0 && w.DroppedFrames == 0;
            clean &= ok;
            Console.WriteLine($"  {(ok ? "OK " : "СБОЙ")} {Name(w.FromId)} → {Name(w.ToId)}: underrun {w.Underruns}, overrun {w.Overruns}, сбросов избытка {w.Resyncs}, потеряно кадров {w.DroppedFrames}, пауз источника {w.Gaps}");
        }
        foreach (var o in engine.Outputs)
            Console.WriteLine($"       {Name(o.Id)}: пусто при пробуждении {o.Starvations}, нет события {o.EventTimeouts}, перезапусков {o.Restarts}");
        var restarts = host.Status.WatchdogRestarts;
        foreach (var s in engine.Sources)
            Console.WriteLine($"       {Name(s.Id)}: разрывов {s.Discontinuities}, перезапусков {s.Restarts}" +
                              (s.IsProcessLoopback ? $", сторожевым таймером {restarts.GetValueOrDefault(s.Id)}" : ""));
        if (_cpuSamples.Count > 0)
            Console.WriteLine($"  CPU после прогрева: среднее {_cpuSamples.Average():F2}%, максимум {_cpuSamples.Max():F2}%");
        if (_maxLatency > 0)
            Console.WriteLine($"  Оценка сквозной задержки по движку (без буферов драйверов): {_minLatency:F1}…{_maxLatency:F1} мс");
        return clean;
    }
}
