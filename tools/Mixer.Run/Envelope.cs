using System.Diagnostics;
using Mixer.Audio.Engine;

namespace Mixer.Run;

/// <summary>
/// Огибающая источника движка (обычно loopback выхода): RMS по 5 мс через отвод. Нужна, чтобы на устройствах
/// замерить разрыв и двойной звук при переключении «прямой маршрут ↔ захват» в самом микшере (v2, этап 3):
/// приложение в прямом маршруте и через микшер одинаково слышно в loopback выхода.
/// </summary>
public sealed class EnvelopeRecorder : IDisposable
{
    public const double WindowMs = 5;

    private readonly SourceNode _src;
    private readonly SpscRing _ring;
    private readonly Thread _thread;
    private readonly Stopwatch _clock;
    private readonly double _startS;
    private readonly int _rate;
    private volatile bool _stop;
    private readonly List<(double T, double Db)> _windows = new();
    private readonly List<(double T, string What)> _events = new();

    public EnvelopeRecorder(SourceNode src, Stopwatch clock)
    {
        _src = src;
        _clock = clock;
        _rate = src.Format!.SampleRate;
        _ring = new SpscRing(_rate * 2);
        _startS = clock.Elapsed.TotalSeconds;
        src.AddTap(_ring);
        _thread = new Thread(Run) { IsBackground = true, Name = "Envelope" };
        _thread.Start();
    }

    /// <summary>Отметить событие (правку графа) по часам прогона.</summary>
    public void Mark(string what)
    {
        lock (_events) _events.Add((_clock.Elapsed.TotalSeconds, what));
    }

    private void Run()
    {
        int win = (int)(_rate * WindowMs / 1000);
        var buf = new float[win * 2];
        long frames = 0;
        while (!_stop)
        {
            Thread.Sleep(10);
            while (_ring.Available >= win)
            {
                _ring.Read(buf, win);
                double sum = 0;
                for (int i = 0; i < buf.Length; i++) sum += buf[i] * (double)buf[i];
                double rms = Math.Sqrt(sum / buf.Length);
                frames += win;
                double t = _startS + frames / (double)_rate;
                lock (_windows) _windows.Add((t, rms > 0 ? 20 * Math.Log10(rms) : -200));
            }
        }
    }

    /// <summary>
    /// Для каждого события: опорный уровень — медиана за 1,5 с до него; разрыв — самая длинная серия окон на 6 дБ
    /// ниже опоры в первые 1,5 с после события; двойной звук — окна на 3 дБ выше опоры.
    /// Опора берётся до события, поэтому для правки, меняющей уровень, «двойной» и «разрыв» сравниваются с прежним.
    /// </summary>
    public void Report(string? csvPath)
    {
        _stop = true;
        _thread.Join(500);
        _src.RemoveTap(_ring);
        List<(double T, double Db)> w;
        lock (_windows) w = _windows.ToList();
        List<(double T, string What)> ev;
        lock (_events) ev = _events.ToList();

        if (csvPath is not null)
        {
            using var csv = new StreamWriter(csvPath);
            csv.WriteLine("time_s;rms_db");
            foreach (var (t, db) in w) csv.WriteLine($"{t:F4};{db:F2}");
            foreach (var (t, what) in ev) csv.WriteLine($"{t:F4};# {what}");
        }

        Console.WriteLine();
        Console.WriteLine($"── Огибающая «{_src.Id}» (окна {WindowMs} мс, {w.Count} окон) ──");
        foreach (var (t0, what) in ev)
        {
            var before = w.Where(x => x.T >= t0 - 1.5 && x.T < t0).Select(x => x.Db).Order().ToList();
            if (before.Count == 0)
            {
                Console.WriteLine($"  {t0,6:F1} с  {what}: нет данных до события");
                continue;
            }
            double reference = before[before.Count / 2];
            var after = w.Where(x => x.T >= t0 && x.T < t0 + 1.5).ToList();
            int run = 0, longest = 0, louder = 0;
            double gapAt = double.NaN;
            foreach (var (t, db) in after)
            {
                if (db < reference - 6)
                {
                    run++;
                    if (run > longest)
                    {
                        longest = run;
                        gapAt = t - t0;
                    }
                }
                else run = 0;
                if (db > reference + 3) louder++;
            }
            var settled = w.Where(x => x.T >= t0 + 1.0 && x.T < t0 + 1.5).Select(x => x.Db).Order().ToList();
            string level = settled.Count > 0 ? $", уровень после {settled[settled.Count / 2]:F1} дБ" : "";
            Console.WriteLine($"  {t0,6:F1} с  {what}: опора {reference:F1} дБ, разрыв макс {longest * WindowMs:F0} мс" +
                              (longest > 0 ? $" (через {gapAt * 1000:F0} мс)" : "") + $", двойной звук {louder * WindowMs:F0} мс{level}");
        }
    }

    public void Dispose()
    {
        _stop = true;
        _src.RemoveTap(_ring);
    }
}
