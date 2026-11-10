using System.Diagnostics;

namespace Mixer.Audio.Engine;

/// <summary>
/// DLL второго порядка (F. Adriaensen, «Using a DLL to filter time»): очищает время событий аудиопотока
/// от задержек пробуждения. Модель: время = начало + кадры × (секунд на кадр), секунд на кадр
/// подстраивается, так что дрейф часов устройства относительно QPC отслеживается сам.
/// Постоянное среднее опоздание остаётся в шкале как сдвиг — на оценку заполнения он не влияет.
/// Скачок больше JumpSeconds (поток стоял, данных не было) сбрасывает шкалу.
/// </summary>
public struct JitterFilter
{
    /// <summary>Полоса DLL по умолчанию, Гц. Джиттер выше неё подавляется.</summary>
    public const double DefaultBandwidthHz = 0.1;
    public const double JumpSeconds = 0.020;
    /// <summary>Столько событий подряд со смещением больше StepSeconds в одну сторону — это ступенька, а не джиттер.</summary>
    public const int StepEvents = 3;
    public const double StepSeconds = 0.003;

    private static readonly double TicksPerSecond = Stopwatch.Frequency;

    private double _hat;      // отфильтрованное время последнего события, тики
    private double _tpf;      // тиков на кадр
    private long _lastFrames;
    private bool _init;
    private double _bandwidth;
    private int _stepRun; // подряд событий со смещением > StepSeconds (знак — направление)

    public void Init(double bandwidthHz)
    {
        _bandwidth = bandwidthHz;
        _init = false;
    }

    public void Reset()
    {
        _init = false;
        _stepRun = 0;
    }

    /// <summary>
    /// Отфильтрованное время (тики Stopwatch) события, наступившего в nowTicks, когда всего пройдено framesTotal кадров
    /// при номинальной частоте rate.
    /// </summary>
    public long Filter(long nowTicks, long framesTotal, int rate)
    {
        if (!_init)
        {
            _hat = nowTicks;
            _tpf = TicksPerSecond / rate;
            _lastFrames = framesTotal;
            _init = true;
            return nowTicks;
        }

        long df = framesTotal - _lastFrames;
        _lastFrames = framesTotal;
        if (df <= 0) return (long)_hat;

        double pred = _hat + df * _tpf;
        double e = nowTicks - pred;
        double step = StepSeconds * TicksPerSecond;
        _stepRun = e > step ? Math.Max(_stepRun, 0) + 1 : e < -step ? Math.Min(_stepRun, 0) - 1 : 0;
        if (Math.Abs(e) > JumpSeconds * TicksPerSecond || Math.Abs(_stepRun) >= StepEvents)
        {
            // разрыв или устойчивая ступенька: начать шкалу заново, скорость оставить
            _hat = nowTicks;
            _stepRun = 0;
            return nowTicks;
        }

        // коэффициенты для текущего шага: ω = 2π·B·T, T — длительность шага
        double w = 2 * Math.PI * _bandwidth * (df / (double)rate);
        double k1 = Math.Sqrt(2) * w, k2 = w * w;
        _hat = pred + k1 * e;
        _tpf += k2 * e / df;
        // скорость не уходит дальше ±2000 ppm от номинала
        double nominal = TicksPerSecond / rate;
        _tpf = Math.Clamp(_tpf, nominal * 0.998, nominal * 1.002);
        return (long)_hat;
    }
}
