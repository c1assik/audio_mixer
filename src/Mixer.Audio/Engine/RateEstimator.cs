namespace Mixer.Audio.Engine;

/// <summary>
/// Фактическая частота потока по QPC: взвешенная линейная регрессия «кадры от времени» с экспоненциальным забыванием.
/// В отличие от DLL, она не сбрасывается на лишних или пропущенных пакетах: у пакетного устройства (пакеты по таймеру,
/// излишек дрейфа — лишним пакетом) они и есть дрейф. Точность растёт с длиной окна: ~±100 ppm за 30 с,
/// единицы ppm за минуты. Без аллокаций.
/// </summary>
public struct RateEstimator
{
    /// <summary>Постоянная забывания, с: за ней оценка следует за медленным (температурным) дрейфом.</summary>
    public const double ForgetSeconds = 600;
    /// <summary>Оценка считается годной, когда охватывает столько секунд.</summary>
    public const double MinSpanSeconds = 30;

    private double _t0, _f0;          // начало координат (сдвигается, чтобы суммы не теряли точность)
    private double _sw, _st, _sf, _stt, _stf;
    private double _first, _last;
    private bool _init;

    public void Reset() => _init = false;

    /// <summary>Сколько секунд покрывает оценка.</summary>
    public readonly double SpanSeconds => _init ? _last - _first : 0;

    public readonly bool Valid => SpanSeconds >= MinSpanSeconds;

    /// <summary>Добавить точку: к моменту seconds (QPC) пройдено frames кадров.</summary>
    public void Add(double seconds, double frames)
    {
        if (!_init)
        {
            _t0 = seconds;
            _f0 = frames;
            _sw = _st = _sf = _stt = _stf = 0;
            _first = _last = seconds;
            _init = true;
        }
        double dt = seconds - _last;
        if (dt > 0)
        {
            double k = Math.Exp(-dt / ForgetSeconds);
            _sw *= k; _st *= k; _sf *= k; _stt *= k; _stf *= k;
        }
        _last = seconds;

        double t = seconds - _t0, f = frames - _f0;
        _sw += 1; _st += t; _sf += f; _stt += t * t; _stf += t * f;

        if (t > 60)
        {
            // сдвиг начала координат: Σ(t−d) = St − d·Sw, Σ(t−d)² = Stt − 2d·St + d²·Sw, Σ(t−d)(f−g) = Stf − d·Sf − g·St + d·g·Sw
            double d = t, g = f;
            _stf = _stf - d * _sf - g * _st + d * g * _sw;
            _stt = _stt - 2 * d * _st + d * d * _sw;
            _st -= d * _sw;
            _sf -= g * _sw;
            _t0 += d;
            _f0 += g;
        }
    }

    /// <summary>Кадров в секунду (по QPC).</summary>
    public readonly double Rate
    {
        get
        {
            double den = _stt - _st * _st / _sw;
            return den > 0 ? (_stf - _st * _sf / _sw) / den : 0;
        }
    }
}
