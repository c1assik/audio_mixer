using System.Globalization;
using Mixer.Probe.Audio;
using Mixer.Probe.Interop;
using Mixer.Probe.Sys;

namespace Mixer.Probe;

internal sealed class Args
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly Dictionary<string, string?> _opts = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; }

    private Args(string command) => Command = command;

    public static Args Parse(string[] argv)
    {
        var a = new Args(argv[0].ToLowerInvariant());
        for (int i = 1; i < argv.Length; i++)
        {
            string t = argv[i];
            if (!t.StartsWith("--", StringComparison.Ordinal))
                throw new UsageException($"Непонятный аргумент «{t}». Параметры пишутся как --имя значение.");
            string? value = null;
            if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal)) value = argv[++i];
            a._opts[t[2..]] = value;
        }
        return a;
    }

    public string Req(string key) =>
        _opts.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw new UsageException($"Нужен параметр --{key}.");

    public string? Opt(string key) => _opts.TryGetValue(key, out var v) ? v : null;

    public bool Flag(string key) => _opts.ContainsKey(key);

    public int Int(string key, int def)
    {
        string? s = Opt(key);
        if (s is null) return def;
        return int.TryParse(s, NumberStyles.Integer, Inv, out int v) ? v : throw new UsageException($"--{key}: ожидается целое число, а не «{s}».");
    }

    public double Dbl(string key, double def)
    {
        string? s = Opt(key);
        if (s is null) return def;
        return double.TryParse(s.Replace(',', '.'), NumberStyles.Float, Inv, out double v) ? v : throw new UsageException($"--{key}: ожидается число, а не «{s}».");
    }
}

internal static class Opts
{
    public static AudioFormat Format(Args a)
    {
        string f = (a.Opt("format") ?? "pcm16").ToLowerInvariant();
        bool isFloat = f switch
        {
            "pcm16" => false,
            "float32" or "float" => true,
            _ => throw new UsageException($"--format: pcm16 или float32, а не «{f}»."),
        };
        return AudioFormat.Simple(a.Int("rate", 48000), a.Int("channels", 2), isFloat);
    }

    /// <summary>Флаги из спецификации: LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM.</summary>
    public static uint ProcessFlags(Args a)
    {
        uint flags = AudioConst.AUDCLNT_STREAMFLAGS_LOOPBACK | AudioConst.AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
        if (!a.Flag("no-autoconvert")) flags |= AudioConst.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM;
        if (a.Flag("src-quality")) flags |= AudioConst.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        return flags;
    }

    public static string OutDir(Args a)
    {
        string dir = Path.GetFullPath(a.Opt("out") ?? Path.Combine(Environment.CurrentDirectory, "probe-out"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static List<ProcInfo> Roots(ProcessSnapshot snap, string exe)
    {
        var roots = snap.Roots(exe);
        if (roots.Count == 0) throw new UsageException($"Процесс {ProcessSnapshot.NormalizeExe(exe)} не запущен.");
        return roots;
    }
}

internal static class Ui
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly ManualResetEventSlim Cancel = new(false);

    /// <summary>Пауза с прерыванием по Ctrl+C. false — прервано.</summary>
    public static bool Sleep(double seconds) => seconds <= 0 || !Cancel.Wait(TimeSpan.FromSeconds(seconds));

    public static string Stamp => DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv);

    public static void Title(string text)
    {
        Console.WriteLine();
        Console.WriteLine("── " + text + " " + new string('─', Math.Max(3, 70 - text.Length)));
    }

    public static string Db(double v) =>
        double.IsNaN(v) ? "NaN" : double.IsNegativeInfinity(v) ? "−∞" : v.ToString("F1", Inv);

    public static string Num(double v, string format = "F0") => v.ToString(format, Inv);

    public static void Warn(string text) => Colored(ConsoleColor.Yellow, "! " + text);

    public static void Check(string text, bool ok, string? note = null) =>
        Colored(ok ? ConsoleColor.Green : ConsoleColor.Red, $"  [{(ok ? "да " : "нет")}] {text}{(note is null ? "" : "  — " + note)}");

    public static void Colored(ConsoleColor color, string text)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = old;
    }

    public static void SummaryHeader() =>
        Console.WriteLine($"  {"",-34}{"сек",6}{"кадров/с",10}{"ожид.",8}{"RMS",8}{"пик",8}{"пакетов",9}{"тихих",7}{"разрывов",10}");

    public static void SummaryRow(string label, Summary s, int expectedRate) =>
        Console.WriteLine($"  {Trim(label, 33),-34}{Num(s.Seconds, "F1"),6}{Num(s.FramesPerSecond),10}{(expectedRate > 0 ? expectedRate.ToString(Inv) : "—"),8}" +
                          $"{Db(s.RmsDb),8}{Db(s.PeakDb),8}{s.Packets,9}{s.SilentPackets,7}{s.Discontinuities,10}");

    public static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public static void Sessions(IEnumerable<SessionInfo> sessions, ProcessSnapshot snap)
    {
        var list = sessions.ToList();
        if (list.Count == 0)
        {
            Console.WriteLine("    (аудиосессий нет)");
            return;
        }
        foreach (var s in list.OrderByDescending(s => s.IsActive).ThenBy(s => s.EndpointName))
            Console.WriteLine($"    PID {s.Pid,-6} {Trim(snap.ExeOf(s.Pid), 24),-24} {Trim(s.EndpointName, 40),-40} {s.StateName,-10} громкость {s.VolumeText}");
    }
}
