using System.Collections.Concurrent;
using System.Diagnostics;

namespace Mixer.Probe.Audio;

/// <summary>
/// Журнал шагов с меткой времени и номером потока плюс сторожевой таймер:
/// если какой-то шаг длится дольше порога, вызывается onHang с перечнем зависших шагов.
/// </summary>
public sealed class StepLog
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<int, (string What, double Since, int Thread)> _inFlight = new();
    private readonly object _write = new();
    private int _next;
    private volatile bool _watching;

    public void Log(string text)
    {
        lock (_write)
            Console.WriteLine($"[{_sw.Elapsed.TotalSeconds,7:F3}] [т{Environment.CurrentManagedThreadId,3}] {text}");
    }

    public T Run<T>(string what, Func<T> f)
    {
        int id = Interlocked.Increment(ref _next);
        _inFlight[id] = (what, _sw.Elapsed.TotalSeconds, Environment.CurrentManagedThreadId);
        Log("→ " + what);
        try
        {
            return f();
        }
        finally
        {
            _inFlight.TryRemove(id, out var started);
            Log($"← {what} ({(_sw.Elapsed.TotalSeconds - started.Since) * 1000:F0} мс)");
        }
    }

    public void Run(string what, Action a) => Run(what, () =>
    {
        a();
        return 0;
    });

    public void StartWatchdog(double timeoutSeconds, Action<IReadOnlyList<string>> onHang)
    {
        _watching = true;
        var t = new Thread(() =>
        {
            while (_watching)
            {
                Thread.Sleep(250);
                double now = _sw.Elapsed.TotalSeconds;
                var stuck = _inFlight.Values.Where(v => now - v.Since > timeoutSeconds)
                    .Select(v => $"т{v.Thread}: «{v.What}» висит {now - v.Since:F1} с").ToList();
                if (stuck.Count > 0)
                {
                    _watching = false;
                    onHang(stuck);
                }
            }
        })
        { IsBackground = true, Name = "watchdog" };
        t.Start();
    }

    public void StopWatchdog() => _watching = false;
}
