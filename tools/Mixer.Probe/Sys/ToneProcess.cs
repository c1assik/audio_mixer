using System.Diagnostics;
using System.Globalization;
using Mixer.Probe.Audio;

namespace Mixer.Probe.Sys;

/// <param name="Device">Устройство (часть имени или ID); null — automatic stream routing на «по умолчанию» приложения.</param>
/// <param name="SeparateExe">Запускать под копией exe (Mixer.ProbeTone.exe). Привязка IAudioPolicyConfig хранится по пути exe,
/// и без копии она задела бы потоки самой утилиты.</param>
/// <param name="WaitStdin">Ждать <see cref="ToneProcess.Go"/> перед открытием потока.</param>
public sealed record ToneOptions(double Seconds, double LevelDb, string? Device = null, bool Noise = false,
    bool SeparateExe = false, bool WaitStdin = false, string? LogPath = null);

/// <summary>Дочерний процесс «tone»: генератор синуса или шума, тестовое «приложение» для замеров этапа 0.</summary>
public sealed class ToneProcess : IDisposable
{
    public const string SeparateExeName = "Mixer.ProbeTone.exe";

    private readonly Process _p;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _started = new(false);
    private readonly TextWriter? _log;
    private long _startTicks;

    public uint Pid => (uint)_p.Id;
    public bool HasExited => _p.HasExited;
    public string ExeName { get; }

    /// <summary>Момент IAudioClient.Start в генераторе (Stopwatch, общий для всех процессов QPC).</summary>
    public long? StartTicks => _started.IsSet ? Interlocked.Read(ref _startTicks) : null;

    private ToneProcess(Process p, string exeName, TextWriter? log)
    {
        _p = p;
        ExeName = exeName;
        _log = log;
    }

    static ToneProcess()
    {
        // Чтение stdout/stderr анонимного канала занимает поток пула на всё время чтения, по два на генератор.
        // Без запаса пул добавляет потоки медленно, канал генератора переполняется, и он замирает на Console.WriteLine
        // посреди цикла воспроизведения: Windows подмешивает тишину, а захват получает нули при ровных 48 000 кадров/с.
        ThreadPool.GetMinThreads(out int worker, out int io);
        ThreadPool.SetMinThreads(Math.Max(worker, 160), Math.Max(io, 160));
    }

    public static ToneProcess Start(ToneOptions o)
    {
        string exe = Environment.ProcessPath!;
        if (o.SeparateExe) exe = EnsureSeparateExe();
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = o.WaitStdin,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        var args = new List<string> { "tone", "--seconds", Ui.Num(o.Seconds, "F1"), "--level", Ui.Num(o.LevelDb, "F1") };
        if (o.Device is not null) args.AddRange(new[] { "--device", o.Device });
        else args.AddRange(new[] { "--routing", "auto" });
        if (o.Noise) args.AddRange(new[] { "--signal", "noise" });
        if (o.WaitStdin) args.Add("--wait-stdin");
        foreach (string a in args) psi.ArgumentList.Add(a);

        var log = o.LogPath is null ? null : TextWriter.Synchronized(new StreamWriter(o.LogPath, append: false) { AutoFlush = true });
        var p = Process.Start(psi) ?? throw new UsageException("Не удалось запустить генератор тона.");
        var t = new ToneProcess(p, Path.GetFileName(exe), log);
        var since = Stopwatch.StartNew();
        p.OutputDataReceived += (_, e) => t.OnLine(e.Data, since);
        p.ErrorDataReceived += (_, e) => t.OnLine(e.Data, since);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return t;
    }

    private void OnLine(string? line, Stopwatch since)
    {
        if (line is null) return;
        _log?.WriteLine($"{since.Elapsed.TotalSeconds,6:F2} {line}");
        if (line.StartsWith("READY", StringComparison.Ordinal)) _ready.Set();
        const string mark = "MARK start qpc=";
        if (line.StartsWith(mark, StringComparison.Ordinal) &&
            long.TryParse(line.AsSpan(mark.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
        {
            Interlocked.Exchange(ref _startTicks, ticks);
            _started.Set();
        }
    }

    public bool WaitReady(TimeSpan timeout) => _ready.Wait(timeout);

    public bool WaitStarted(TimeSpan timeout) => _started.Wait(timeout);

    /// <summary>Разрешить генератору, запущенному с WaitStdin, открыть поток.</summary>
    public void Go()
    {
        _p.StandardInput.WriteLine("go");
        _p.StandardInput.Flush();
    }

    /// <summary>Копия apphost под другим именем: он грузит Mixer.Probe.dll из своей папки, так что копия работает как есть.</summary>
    private static string EnsureSeparateExe()
    {
        string src = Environment.ProcessPath!;
        string dst = Path.Combine(Path.GetDirectoryName(src)!, SeparateExeName);
        if (!File.Exists(dst) || File.GetLastWriteTimeUtc(dst) < File.GetLastWriteTimeUtc(src)) File.Copy(src, dst, overwrite: true);
        return dst;
    }

    public void Dispose()
    {
        try
        {
            if (!_p.HasExited)
            {
                _p.Kill();
                _p.WaitForExit(2000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        _p.Dispose();
        _log?.Dispose();
    }
}
