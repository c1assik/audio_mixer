using Mixer.Plugins.Host;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Mixer.PluginBench;

internal static class Program
{
    private const string Usage = """
        Mixer.PluginBench — поддержка VST: стоимость обмена с Mixer.PluginHost.exe и память хоста (этап 0),
        ручная проверка окна редактора (этап 1), скан плагинов (этап 2).

          Mixer.PluginBench [--plugin <path.vst3> | --builtin] [--frames 480,128] [--minutes 60]
          Mixer.PluginBench --editor [--plugin <path.vst3>] [--png <file.png>] [--close] [--seconds 60]
                            [--x <px> --y <px> [--w <px> --h <px>]]

          --host <exe>         Mixer.PluginHost.exe (по умолчанию src/Mixer.PluginHost/build/bin)
          --plugin <path>      плагин VST3 (по умолчанию again.vst3 из сборки хоста)
          --builtin            без плагина: хост копирует вход в выход
          --frames <list>      размеры блоков через запятую; каждый — свой поток и свой хост, параллельно
          --rate <hz>          частота, по умолчанию 48000
          --minutes <m>        длительность (можно дробно), по умолчанию 1
          --deadline-pct <p>   дедлайн, % периода, по умолчанию 40
          --spin-us <us>       активное ожидание ответа, мкс, по умолчанию 50
          --gate-us <us>       ворота этапа: p99 обмена не больше, мкс, по умолчанию 100
          --stall-every <n> --stall-ms <ms>   хост спит каждый n-й блок (проверка дедлайна)
          --out <file.json>    куда записать результат (по умолчанию probe-out/pluginbench-*.json)

          --editor             открыть окно плагина (владелец — окно консоли), напечатать события и геометрию
          --png <file>         снять окно в PNG
          --close              закрыть окно самому, не ждать пользователя
          --x --y --w --h      запомненные положение и размер окна (размер — если плагин его меняет)

          --scan [--folder <dir>]... [--full]   этап 2: скан стандартных папок VST3 с кэшем микшера
                               (%LocalAppData%\Mixer\plugins.json), каталог и списки проблем; --full — без кэша
          --compat [--folder <dir>]... [--only <имя>] [--out <file.md>]
                               этап 5: проверка совместимости каждого эффекта каталога (загрузка, звук, неровные
                               блоки, 44,1 кГц, окно, состояние после перезапуска); таблица для docs/DEVELOPMENT.md
        """;

    private static int Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (argv.Any(a => a is "-h" or "--help" or "/?"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        Options o;
        try { o = Options.Parse(argv); }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (o.Compat)
            return CompatCheck.Run(o.HostExe, o.Folders, o.Only, o.OutPath);
        if (o.Scan)
            return ScanCheck.Run(o.HostExe, o.Folders, o.Full);
        if (o.Editor)
            return EditorCheck.Run(o.HostExe, o.PluginPath!, o.Seconds, o.Png, o.Bounds, o.Close);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        string plugin = o.PluginPath is null ? "builtin copy" : Path.GetFileName(o.PluginPath.TrimEnd('\\', '/'));
        Console.WriteLine($"Mixer.PluginBench · {plugin} · {string.Join(", ", o.Frames)} кадров @ {o.Rate} Гц · {o.Minutes:0.##} мин");
        Console.WriteLine($"{CpuName()} · {Environment.ProcessorCount} логических ядер · {Environment.OSVersion.VersionString}");
        Console.WriteLine($"хост: {o.HostExe}");

        var channels = new List<HostChannel>();
        var runs = new List<BenchRun>();
        var memory = new List<MemoryWatch>();
        try
        {
            foreach (int frames in o.Frames)
            {
                var ch = HostChannel.Start(new HostStartInfo(o.HostExe, o.PluginPath, o.Rate, 8192, o.StallEvery, o.StallMs), o.SpinUs);
                channels.Add(ch);
                memory.Add(new MemoryWatch(ch.HostProcess));
                Console.WriteLine($"  [{frames}] хост pid {ch.HostProcess.Id} готов за {ch.StartupTime.TotalMilliseconds:0} мс; " +
                                  $"задержка {ch.LatencySamples}, каналы {ch.PluginInChannels}→{ch.PluginOutChannels}; " +
                                  $"память {Mb(memory[^1].PrivateAtReady)} частной, {Mb(memory[^1].WorkingSetAtReady)} рабочий набор");
                runs.Add(new BenchRun($"{frames}", ch, frames, o.Rate, o.DeadlinePct, TimeSpan.FromMinutes(o.Minutes), o.GateUs, cts.Token));
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"хост не запустился: {e.Message}");
            foreach (var c in channels) c.Dispose();
            return 3;
        }

        var wall = Stopwatch.StartNew();
        foreach (var r in runs) r.Start();

        var lastProgress = TimeSpan.Zero;
        var lastSample = TimeSpan.Zero;
        while (!runs.All(r => r.Finished))
        {
            Thread.Sleep(250);
            if (wall.Elapsed - lastSample >= TimeSpan.FromSeconds(5))
            {
                lastSample = wall.Elapsed;
                foreach (var m in memory) m.Sample();
            }
            if (wall.Elapsed - lastProgress >= TimeSpan.FromMinutes(1))
            {
                lastProgress = wall.Elapsed;
                foreach (var r in runs)
                    Console.WriteLine($"  {wall.Elapsed:hh\\:mm\\:ss} [{r.Label}] блоков {r.Blocks}, обмен p99 {r.Overhead.Percentile(99):0.0} мкс, " +
                                      $"max {r.Overhead.MaxUs:0} мкс, просрочек {r.Missed}");
            }
            if (channels.Any(c => c.HostProcess.HasExited)) cts.Cancel();
        }
        foreach (var r in runs) r.Join();
        foreach (var m in memory) m.Finish();

        var report = new List<object>();
        bool pass = true;
        for (int i = 0; i < runs.Count; i++)
        {
            var r = runs[i];
            var m = memory[i];
            var ch = channels[i];
            bool hostDied = ch.HostProcess.HasExited;
            bool runPass = r.Error == null && !hostDied && r.Overhead.Percentile(99) <= o.GateUs && r.Mismatches == 0;
            pass &= runPass;
            PrintRun(r, m, o, runPass, hostDied ? ch.Stderr() : null);
            report.Add(RunJson(r, m, ch, o, runPass));
        }
        foreach (var c in channels) c.Dispose();

        string outPath = o.OutPath ?? Path.Combine(Paths.RepoRoot() ?? ".", "probe-out",
            $"pluginbench-{DateTime.Now:yyyyMMdd-HHmmss}-{string.Join("_", o.Frames)}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        var doc = new
        {
            date = DateTime.Now.ToString("s"),
            machine = new { cpu = CpuName(), logicalCores = Environment.ProcessorCount, os = Environment.OSVersion.VersionString },
            plugin,
            rate = o.Rate,
            minutes = o.Minutes,
            spinUs = o.SpinUs,
            deadlinePct = o.DeadlinePct,
            gateUs = o.GateUs,
            pass,
            runs = report,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"результат: {Path.GetFullPath(outPath)}");
        Console.WriteLine(pass ? "ВОРОТА ПРОЙДЕНЫ" : "ВОРОТА НЕ ПРОЙДЕНЫ");
        return pass ? 0 : 1;
    }

    private static void PrintRun(BenchRun r, MemoryWatch m, Options o, bool pass, string? hostStderr)
    {
        Console.WriteLine();
        Console.WriteLine($"[{r.Frames} кадров · период {r.PeriodMs:0.###} мс · дедлайн {r.DeadlineMs:0.###} мс] {r.Elapsed:hh\\:mm\\:ss}");
        if (r.Error != null) Console.WriteLine($"  ОШИБКА: {r.Error}");
        if (hostStderr != null) Console.WriteLine($"  хост завершился: {hostStderr}");
        Console.WriteLine($"  блоков {r.Blocks}: обработано {r.Processed}, просрочено {r.Missed}, хост занят {r.Busy}, " +
                          $"расхождений {r.Mismatches}, ошибок process {r.BadResults}, отставаний потока {r.Overruns}");
        if (r.Missed > 0)
            Console.WriteLine($"  просрочены блоки №: {string.Join(", ", r.MissedAt.Take((int)Math.Min(r.Missed, r.MissedAt.Length)))}");
        double spinShare = r.Processed == 0 ? 0 : 100.0 * r.SpinHits / r.Processed;
        Console.WriteLine($"  ответ пойман активным ожиданием: {spinShare:0.0}%");
        Console.WriteLine("  мкс              mean     p50     p90     p99   p99.9  p99.99     max");
        Row("обмен", r.Overhead);
        Row("весь блок", r.RoundTrip);
        Row("  → хост", r.HostWake);
        Row("  ← назад", r.Return);
        Row("process", r.ProcessTime);
        Row("таймер", r.TimerLate);
        long over = r.Overhead.CountAbove(o.GateUs);
        Console.WriteLine($"  обмен > {o.GateUs:0} мкс: {over} блоков ({(r.Processed == 0 ? 0 : 100.0 * over / r.Processed):0.000}%), " +
                          $"худшая минута {r.OverGatePerMinute.Max()}");
        double hostCore = m.HostCpuSeconds / r.Elapsed.TotalSeconds * 100;
        double benchCore = r.ThreadCpuSeconds / r.Elapsed.TotalSeconds * 100;
        double exchangeCore = ExchangeCorePct(r);
        Console.WriteLine($"  CPU одного ядра: хост {hostCore:0.000}%, аудиопоток микшера в обмене ≤ {exchangeCore:0.000}% " +
                          $"(весь поток бенчмарка с таймером {benchCore:0.000}%); " +
                          $"обмен от всех {Environment.ProcessorCount} ядер ≤ {(hostCore + exchangeCore) / Environment.ProcessorCount:0.0000}%");
        Console.WriteLine($"  память хоста: частная {Mb(m.PrivateAtReady)} → {Mb(m.PrivateAtEnd)} (макс {Mb(m.PrivateMax)}), " +
                          $"рабочий набор {Mb(m.WorkingSetAtReady)} → {Mb(m.WorkingSetAtEnd)} (пик {Mb(m.PeakWorkingSet)})");
        Console.WriteLine($"  ворота: p99 обмена {r.Overhead.Percentile(99):0.0} мкс ≤ {o.GateUs:0} мкс — {(pass ? "да" : "НЕТ")}");
    }

    private static void Row(string name, Histogram h) =>
        Console.WriteLine($"  {name,-12} {h.MeanUs,7:0.0} {h.Percentile(50),7:0.0} {h.Percentile(90),7:0.0} {h.Percentile(99),7:0.0} " +
                          $"{h.Percentile(99.9),7:0.0} {h.Percentile(99.99),7:0.0} {h.MaxUs,7:0}");

    private static object RunJson(BenchRun r, MemoryWatch m, HostChannel ch, Options o, bool pass) => new
    {
        frames = r.Frames,
        periodMs = r.PeriodMs,
        deadlineMs = r.DeadlineMs,
        seconds = Math.Round(r.Elapsed.TotalSeconds, 1),
        error = r.Error?.Message,
        pass,
        blocks = r.Blocks,
        processed = r.Processed,
        missed = r.Missed,
        busy = r.Busy,
        mismatches = r.Mismatches,
        badResults = r.BadResults,
        overruns = r.Overruns,
        spinHits = r.SpinHits,
        hostStartupMs = Math.Round(ch.StartupTime.TotalMilliseconds),
        latencySamples = ch.LatencySamples,
        us = new
        {
            exchange = r.Overhead.Summary(),
            roundTrip = r.RoundTrip.Summary(),
            hostWake = r.HostWake.Summary(),
            returnPath = r.Return.Summary(),
            process = r.ProcessTime.Summary(),
            timerLate = r.TimerLate.Summary(),
        },
        exchangeOverGate = r.Overhead.CountAbove(o.GateUs),
        overGatePerMinute = r.OverGatePerMinute.Take((int)Math.Ceiling(r.Elapsed.TotalMinutes)).ToArray(),
        maxExchangePerMinuteUs = r.MaxOverheadPerMinute.Take((int)Math.Ceiling(r.Elapsed.TotalMinutes)).Select(v => Math.Round(v, 1)).ToArray(),
        cpu = new
        {
            hostCoreSeconds = Math.Round(m.HostCpuSeconds, 3),
            mixerThreadCoreSeconds = Math.Round(r.ThreadCpuSeconds, 3),
            hostPctOfCore = Math.Round(m.HostCpuSeconds / r.Elapsed.TotalSeconds * 100, 4),
            mixerThreadPctOfCore = Math.Round(r.ThreadCpuSeconds / r.Elapsed.TotalSeconds * 100, 4),
            mixerExchangePctOfCoreUpperBound = Math.Round(ExchangeCorePct(r), 4),
            exchangePctOfAllCoresUpperBound = Math.Round((m.HostCpuSeconds / r.Elapsed.TotalSeconds * 100 + ExchangeCorePct(r)) / Environment.ProcessorCount, 5),
        },
        missedAtBlock = r.MissedAt.Take((int)Math.Min(r.Missed, r.MissedAt.Length)).ToArray(),
        memoryBytes = new
        {
            privateAtReady = m.PrivateAtReady,
            privateAtEnd = m.PrivateAtEnd,
            privateMax = m.PrivateMax,
            workingSetAtReady = m.WorkingSetAtReady,
            workingSetAtEnd = m.WorkingSetAtEnd,
            peakWorkingSet = m.PeakWorkingSet,
        },
    };

    private static double ExchangeCorePct(BenchRun r) =>
        r.ExchangeWallTicks / (double)Stopwatch.Frequency / r.Elapsed.TotalSeconds * 100;

    private static string Mb(long bytes) => $"{bytes / 1048576.0:0.0} МБ";

    private static string CpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        }
        catch { return "CPU"; }
    }
}

/// <summary>Память и CPU процесса хоста: снимок после загрузки, максимум за прогон, снимок в конце.</summary>
internal sealed class MemoryWatch
{
    private readonly Process _p;
    private readonly TimeSpan _cpuAtReady;

    public long PrivateAtReady { get; }
    public long WorkingSetAtReady { get; }
    public long PrivateMax { get; private set; }
    public long PrivateAtEnd { get; private set; }
    public long WorkingSetAtEnd { get; private set; }
    public long PeakWorkingSet { get; private set; }
    public double HostCpuSeconds { get; private set; }

    public MemoryWatch(Process p)
    {
        _p = p;
        p.Refresh();
        PrivateAtReady = PrivateMax = p.PrivateMemorySize64;
        WorkingSetAtReady = p.WorkingSet64;
        _cpuAtReady = p.TotalProcessorTime;
    }

    public void Sample()
    {
        if (_p.HasExited) return;
        _p.Refresh();
        PrivateMax = Math.Max(PrivateMax, _p.PrivateMemorySize64);
    }

    public void Finish()
    {
        if (_p.HasExited) return;
        _p.Refresh();
        PrivateAtEnd = _p.PrivateMemorySize64;
        PrivateMax = Math.Max(PrivateMax, PrivateAtEnd);
        WorkingSetAtEnd = _p.WorkingSet64;
        PeakWorkingSet = _p.PeakWorkingSet64;
        HostCpuSeconds = (_p.TotalProcessorTime - _cpuAtReady).TotalSeconds;
    }
}

internal static class Paths
{
    public static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Mixer.sln"))) return d.FullName;
        return null;
    }
}

internal sealed class Options
{
    public string HostExe = "";
    public string? PluginPath;
    public int[] Frames = [480];
    public int Rate = 48000;
    public double Minutes = 1;
    public double DeadlinePct = 40;
    public double SpinUs = 50;
    public double GateUs = 100;
    public int StallEvery, StallMs;
    public string? OutPath;
    public bool Editor, Close;
    public double Seconds = 60;
    public string? Png;
    public EditorBounds? Bounds;
    public bool Scan, Full, Compat;
    public string? Only;
    public List<string> Folders = [];

    public static Options Parse(string[] argv)
    {
        var o = new Options();
        string? host = null, plugin = null;
        bool builtin = false;
        int? x = null, y = null;
        int w = 0, h = 0;
        for (int i = 0; i < argv.Length; i++)
        {
            string Next() => i + 1 < argv.Length ? argv[++i] : throw new ArgumentException($"{argv[i]}: нет значения");
            double Num() => double.Parse(Next(), CultureInfo.InvariantCulture);
            switch (argv[i])
            {
                case "--host": host = Next(); break;
                case "--plugin": plugin = Next(); break;
                case "--builtin": builtin = true; break;
                case "--frames": o.Frames = Next().Split(',').Select(int.Parse).ToArray(); break;
                case "--rate": o.Rate = (int)Num(); break;
                case "--minutes": o.Minutes = Num(); break;
                case "--deadline-pct": o.DeadlinePct = Num(); break;
                case "--spin-us": o.SpinUs = Num(); break;
                case "--gate-us": o.GateUs = Num(); break;
                case "--stall-every": o.StallEvery = (int)Num(); break;
                case "--stall-ms": o.StallMs = (int)Num(); break;
                case "--out": o.OutPath = Next(); break;
                case "--editor": o.Editor = true; break;
                case "--scan": o.Scan = true; break;
                case "--full": o.Full = true; break;
                case "--compat": o.Compat = true; break;
                case "--only": o.Only = Next(); break;
                case "--folder": o.Folders.Add(Next()); break;
                case "--close": o.Close = true; break;
                case "--seconds": o.Seconds = Num(); break;
                case "--png": o.Png = Next(); break;
                case "--x": x = (int)Num(); break;
                case "--y": y = (int)Num(); break;
                case "--w": w = (int)Num(); break;
                case "--h": h = (int)Num(); break;
                default: throw new ArgumentException($"неизвестный аргумент {argv[i]}");
            }
        }
        if (o.Frames.Any(f => f < 1 || f > 8192)) throw new ArgumentException("--frames: от 1 до 8192");
        if (x is { } bx && y is { } by) o.Bounds = new EditorBounds(bx, by, w, h);
        if (o.Editor && builtin) throw new ArgumentException("--editor: нужен настоящий плагин, у встроенных окна нет");

        string? build = Paths.RepoRoot() is { } root ? Path.Combine(root, "src", "Mixer.PluginHost", "build") : null;
        o.HostExe = host ?? (build != null ? Path.Combine(build, "bin", "Mixer.PluginHost.exe") : "Mixer.PluginHost.exe");
        if (!File.Exists(o.HostExe)) throw new ArgumentException($"нет хоста {o.HostExe}: соберите tools/build-pluginhost.ps1");
        if (!builtin && !o.Scan && !o.Compat)
        {
            o.PluginPath = plugin ?? (build != null ? Path.Combine(build, "VST3", "Release", "again.vst3") : null);
            if (o.PluginPath == null || !Path.Exists(o.PluginPath)) throw new ArgumentException($"нет плагина {o.PluginPath}");
        }
        return o;
    }
}
