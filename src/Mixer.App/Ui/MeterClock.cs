using System.Windows.Media;

namespace Mixer.App.Ui;

/// <summary>
/// Такт индикаторов: CompositionTarget.Rendering, не чаще 30 кадров в секунду. Сначала Update (модель забирает
/// пики из движка), потом Render (индикаторы и провода перерисовываются, если изменились). Окно свёрнуто — такт
/// остановлен, чтобы не тратить процессор.
/// </summary>
public static class MeterClock
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(1000.0 / 30 - 1);
    private static TimeSpan _last = TimeSpan.MinValue;
    private static bool _running;

    /// <summary>(секунды с запуска, секунды с прошлого кадра)</summary>
    public static event Action<double, double>? Update;
    public static event Action? Render;

    public static void Start()
    {
        if (_running) return;
        _running = true;
        _last = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnRendering;
    }

    public static void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        var t = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (_last != TimeSpan.MinValue && t - _last < Frame) return;
        double dt = _last == TimeSpan.MinValue ? 1.0 / 30 : Math.Min(0.25, (t - _last).TotalSeconds);
        _last = t;
        Update?.Invoke(t.TotalSeconds, dt);
        Render?.Invoke();
    }
}
