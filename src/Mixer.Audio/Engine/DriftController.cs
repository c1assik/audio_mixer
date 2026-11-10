namespace Mixer.Audio.Engine;

/// <summary>
/// PI-регулятор поправки ε к коэффициенту ресемплера по заполнению кольца провода. Два режима:
/// <list type="bullet">
/// <item><b>Захват</b> — быстрый: заполнение сглаживается за ~1 с, дрейф +300 ppm сходится за ~9 с (требование спецификации).</item>
/// <item><b>Слежение</b> — после сходимости: медленное сглаживание и малые коэффициенты. Нужен для устройств, которые
/// отдают пакеты по своему таймеру, а излишек от дрейфа — лишним пакетом раз в несколько секунд
/// (USB-микрофон этапа 1: +566 ppm, пакет 10 мс раз в 17,7 с). Кольцо тогда живёт «пилой» ±5 мс, и быстрый
/// регулятор гонялся бы за ней на ±1000 ppm, а правильный ответ — постоянная ε, равная среднему дрейфу.</item>
/// </list>
/// В слежении ε = прямая оценка дрейфа (отношение фактических частот сторон, см. RateEstimator) + малый PI по заполнению.
/// Ошибка считается в секундах, ε ограничена ±MaxPpm. Работает в потоке выхода, без аллокаций.
/// </summary>
public struct DriftController
{
    // Захват: подобраны перебором на модели провода (TuningSweep).
    public const double DefaultKp = 0.8;          // 1/с: 1 мс избытка → +800 ppm
    public const double DefaultKi = 0.08;         // 1/с²
    public const double DefaultSmoothingSeconds = 1.0;
    public const double DefaultMaxPpm = 1000;
    /// <summary>
    /// Предел прямой оценки дрейфа в слежении. Шире MaxPpm: VB-Cable на этапе 1 при двух выходах забирал звук
    /// на 1% медленнее реального времени, и с ±1000 ppm провод терял звук сбросами каждые 2 с.
    /// </summary>
    public const double DefaultMaxDriftPpm = 20000;

    // Слежение: подобраны на модели пакетного источника (TuningSweep.Tracking).
    public const double DefaultTrackKp = 0.05;
    public const double DefaultTrackKi = 0.001;
    public const double DefaultTrackSmoothingSeconds = 5.0;
    /// <summary>Слежение включается не раньше чем через столько секунд захвата и когда готова прямая оценка дрейфа.</summary>
    public const double DefaultAcquireSeconds = 15;

    private double _kp, _ki, _tau, _max, _maxDrift;
    private double _tkp, _tki, _ttau, _acquireSeconds;
    private double _smoothed;
    private double _integral;
    private double _elapsed;
    private bool _primed;

    /// <summary>Текущая поправка (1e-6 = 1 ppm).</summary>
    public double Epsilon { get; private set; }

    /// <summary>Сглаженная ошибка заполнения в секундах (плюс — кольцо полнее цели).</summary>
    public double ErrorSeconds { get; private set; }

    public double SmoothedFill => _smoothed;

    /// <summary>Режим слежения включён.</summary>
    public bool Tracking { get; private set; }

    public void Init(EngineOptions o)
    {
        _kp = o.Kp;
        _ki = o.Ki;
        _tau = o.FillSmoothingSeconds;
        _max = o.MaxPpm * 1e-6;
        _maxDrift = Math.Max(o.MaxDriftPpm, o.MaxPpm) * 1e-6;
        _tkp = o.TrackKp;
        _tki = o.TrackKi;
        _ttau = o.TrackSmoothingSeconds;
        _acquireSeconds = o.AcquireSeconds;
        Reset();
    }

    /// <summary>
    /// После разрыва (перезапуск источника, сброс избытка): заново сгладить заполнение и вернуться в захват,
    /// но накопленную поправку оставить — дрейф часов от этого не меняется.
    /// </summary>
    public void Restart()
    {
        _primed = false;
        _elapsed = 0;
        if (Tracking)
        {
            // назад в захват без скачка ε: сейчас e≈0 после пересглаживания, интеграл = ε
            _integral = Epsilon;
            Tracking = false;
        }
    }

    /// <summary>
    /// После сброса избытка или переполнения: только заново сгладить заполнение. Режим, накопленная поправка
    /// и таймер захвата остаются — иначе при частых сбросах (выход с дрейфом за ±MaxPpm) слежение не включится никогда.
    /// </summary>
    public void Resynced() => _primed = false;

    public void Reset()
    {
        _primed = false;
        _integral = 0;
        _elapsed = 0;
        Tracking = false;
        Epsilon = 0;
        ErrorSeconds = 0;
    }

    /// <summary>
    /// fillFrames — заполнение кольца во входных кадрах, targetFrames — цель, inputRate — частота источника,
    /// dt — длительность прошедшего блока выхода в секундах; drift — прямая оценка дрейфа, если она уже готова.
    /// </summary>
    public double Update(double fillFrames, double targetFrames, double inputRate, double dt, bool driftValid = false, double drift = 0)
    {
        double tau = Tracking ? _ttau : _tau;
        if (!_primed)
        {
            _smoothed = fillFrames;
            _primed = true;
        }
        else
        {
            double a = dt >= tau ? 1 : 1 - Math.Exp(-dt / tau);
            _smoothed += a * (fillFrames - _smoothed);
        }

        double e = (_smoothed - targetFrames) / inputRate;
        ErrorSeconds = e;
        _elapsed += dt;

        if (!Tracking && _elapsed >= _acquireSeconds && driftValid)
        {
            // Интеграл захвата у пакетного источника искажён ступеньками (в момент перехода ε могла стоять в +1000
            // посреди слива), поэтому в слежении основу даёт прямая оценка, а интеграл начинает с нуля.
            // Скачок ε в момент перехода — сотни ppm, доли цента: не слышно.
            Tracking = true;
            _integral = 0;
        }

        double kp = Tracking ? _tkp : _kp, ki = Tracking ? _tki : _ki;
        // Прямая оценка может выходить за ±MaxPpm (до ±MaxDriftPpm), а PI-добавка к ней — нет.
        double ff = Tracking && driftValid ? Math.Clamp(drift, -_maxDrift, _maxDrift) : 0;
        double lo = Math.Max(ff - _max, -_maxDrift), hi = Math.Min(ff + _max, _maxDrift);
        if (!Tracking)
        {
            lo = -_max;
            hi = _max;
        }
        // Защита от раскрутки интегратора: пока выход упёрт в предел и ошибка тянет туда же, интеграл не копится.
        // Иначе после ступеньки в 10 мс интеграл насыщается, проскакивает цель, и ε десятки секунд ходит между пределами.
        double next = Math.Clamp(_integral + ki * e * dt, -_max, _max);
        double u = ff + kp * e + next;
        bool saturated = (u > hi && e > 0) || (u < lo && e < 0);
        if (!saturated) _integral = next;
        Epsilon = Math.Clamp(ff + kp * e + _integral, lo, hi);
        return Epsilon;
    }
}
