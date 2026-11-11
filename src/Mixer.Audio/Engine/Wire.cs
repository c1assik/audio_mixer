using System.Diagnostics;
using Mixer.Audio.Dsp;

namespace Mixer.Audio.Engine;

/// <summary>
/// Часы источника, общие для всех его проводов. Поток источника меняет их при каждом (пере)запуске:
/// сначала SampleRate, затем Epoch. SampleRate = 0 — источник не работает.
/// </summary>
public sealed class SourceClock
{
    private int _rate;
    private int _epoch;
    private int _breaks;
    private long _periodBits;

    /// <summary>
    /// Источник с законными паузами (process loopback: приложение замолчало — пакетов нет).
    /// Пауза дольше <see cref="Wire.GapSeconds"/> — не underrun, а «пауза источника», и в оценку частоты она не входит.
    /// </summary>
    public bool Intermittent { get; init; }

    public int SampleRate => Volatile.Read(ref _rate);
    /// <summary>Период источника, с (0 — обычный, <see cref="EngineOptions.PeriodMs"/>).</summary>
    public double PeriodSeconds => BitConverter.Int64BitsToDouble(Volatile.Read(ref _periodBits));
    public int Epoch => Volatile.Read(ref _epoch);
    /// <summary>Разрывы потока источника (DATA_DISCONTINUITY): связь «кадры — время» сместилась скачком.</summary>
    public int Breaks => Volatile.Read(ref _breaks);

    public void Break() => Interlocked.Increment(ref _breaks);

    public void Started(int sampleRate, double periodSeconds = 0)
    {
        Volatile.Write(ref _periodBits, BitConverter.DoubleToInt64Bits(periodSeconds));
        Volatile.Write(ref _rate, sampleRate);
        Interlocked.Increment(ref _epoch);
    }

    public void Stopped()
    {
        Volatile.Write(ref _rate, 0);
        Interlocked.Increment(ref _epoch);
    }
}

public enum WireState { Idle, Priming, Running }

/// <summary>
/// Провод источник → выход: своё SPSC-кольцо, свой адаптивный ресемплер с PI-регулятором,
/// своё усиление и индикатор. Пишет поток источника (Write), читает поток выхода (Render).
/// </summary>
public sealed class Wire
{
    public const double FadeSeconds = 0.002;
    /// <summary>Наименьший фейд: у малой цели заполнения фейд — её четверть, но не короче этого.</summary>
    public const double MinFadeSeconds = 0.001;
    /// <summary>Тишина у прерывистого источника дольше этого — пауза, а не сбой.</summary>
    public const double GapSeconds = 0.1;

    private readonly SourceClock _clock;
    private readonly EngineOptions _opt;
    private readonly SpscRing _ring;

    // ── опубликованное писателем: время последней записи (seqlock) ──
    private int _seq;
    private long _lastWriteTicks;
    private int _lastWriteFrames;
    private volatile bool _readerActive;
    // писатель: своя шкала времени без джиттера пробуждений
    private JitterFilter _wJitter;
    private long _wFrames;
    private int _wEpoch = -1;
    private int _wRateNominal;
    private int _wBreaks;
    private long _wTotal;              // кадры с запуска источника (на разрывах не сбрасываются)
    private RateEstimator _wRate;
    private double _wRateHz;           // опубликовано для читателя
    private volatile bool _wRateValid;
    private long _wLastRawTicks;       // время последней записи без фильтра (для пауз прерывистого источника)
    private int _wLastRawFrames;
    private double _wPausedSeconds;    // сумма пауз: вычитается из времени оценки частоты

    // ── параметры от управляющего потока ──
    private volatile float _targetGain = 1f;
    private volatile bool _enabled = true;

    // ── состояние читателя (только поток выхода) ──
    private bool _active;
    private int _outRate;
    private double _outPeriod;
    private int _epoch = -1;
    private int _inRate;
    private int _rsOutRate;
    private Resampler? _rs;
    private DriftController _ctl;
    private StereoGain _gain;
    private WireState _state;
    private double _targetFrames;
    private double _resyncSeconds;
    private double _trackResyncSeconds;
    private int _overflowFrames;
    private int _fadeFrames;
    private bool _smallTarget;         // цель ниже обычной из-за малых периодов: underrun — сбой «Низкой задержки»
    private int _fadeInLeft;
    private JitterFilter _rJitter;
    private RateEstimator _rRate;
    private int _breaksAtStart;
    private bool _targetRaised;
    private int _breaksSeen;   // разрывов источника, замеченных читателем
    private long _reads, _lastBreakRead;
    private double _baseTargetFrames;
    private long _traceNow;
    private long _rFrames;
    private bool _dryPending;          // кольцо опустело у прерывистого источника: underrun или пауза — решится позже
    private long _dryLastWrite;

    // ── статистика (читается из любого потока) ──
    private int _underruns;
    private int _gaps;
    private int _overruns;
    private int _resyncs;
    private long _droppedFrames;
    private double _fillMs;
    private double _epsPpm;
    private double _driftPpm = double.NaN;
    private double _latencyMs;
    private volatile WireState _publicState;
    private volatile bool _tracking;

    /// <summary>Трассировка событий (null — выключена).</summary>
    public WireTrace? Trace { get; set; }

    public string FromId { get; }
    /// <summary>Куда провод ведёт: выход или шина.</summary>
    public string ToId { get; }
    /// <summary>Выход, поток которого читает провод (у провода в шину — выход, для которого посчитан экземпляр шины).</summary>
    public string OutputId { get; }
    public PeakMeter Peak { get; } = new();

    public Wire(string fromId, string toId, SourceClock clock, EngineOptions options, string? outputId = null)
    {
        FromId = fromId;
        ToId = toId;
        OutputId = outputId ?? toId;
        _clock = clock;
        _opt = options;
        _ring = new SpscRing(options.RingCapacityFrames);
        _ctl.Init(options);
        _wJitter.Init(options.TimelineBandwidthHz);
        _rJitter.Init(options.TimelineBandwidthHz);
    }

    // ── управляющий поток ───────────────────────────────────────

    public float GainDb { set => _targetGain = Db.ToGain(value); }
    public bool Enabled { get => _enabled; set => _enabled = value; }

    public int Underruns => Volatile.Read(ref _underruns);
    /// <summary>Паузы прерывистого источника (приложение замолчало). Не сбой: кольцо опустело, потому что звука не было.</summary>
    public int Gaps => Volatile.Read(ref _gaps);
    /// <summary>Переполнения: сколько раз читатель сбрасывал накопившееся до цели.</summary>
    public int Overruns => Volatile.Read(ref _overruns);
    /// <summary>Сбросы избытка: заполнение ушло выше цели больше чем на ResyncMs, лишнее выброшено с фейдом.</summary>
    public int Resyncs => Volatile.Read(ref _resyncs);
    /// <summary>Кадры, не влезшие в кольцо у писателя (выход стоял дольше ёмкости кольца).</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    /// <summary>Сглаженное заполнение кольца, мс.</summary>
    public double FillMs => Volatile.Read(ref _fillMs);
    public double EpsilonPpm => Volatile.Read(ref _epsPpm);
    /// <summary>Прямая оценка дрейфа источника относительно выхода, ppm (NaN — ещё не готова).</summary>
    public double DriftPpm => Volatile.Read(ref _driftPpm);
    /// <summary>Цель заполнения сейчас, мс (поднимается на пакет у источника с пропусками).</summary>
    public double TargetMs => _inRate > 0 ? _targetFrames * 1000.0 / _inRate : 0;
    /// <summary>Регулятор в режиме слежения (сошёлся).</summary>
    public bool Tracking => _tracking;
    /// <summary>Задержка самого провода: кольцо плюс ресемплер, мс.</summary>
    public double LatencyMs => Volatile.Read(ref _latencyMs);
    public WireState State => _publicState;
    /// <summary>Сырое заполнение кольца прямо сейчас, мс (для диагностики; без сглаживания и поправки на время).</summary>
    public double RawFillMs => _inRate > 0 ? _ring.Available * 1000.0 / _inRate : 0;
    public int InputRate => _clock.SampleRate;
    public int OutputRate => Volatile.Read(ref _outRate);

    // ── писатель (поток источника) ──────────────────────────────

    public void Write(ReadOnlySpan<float> stereo, int frames, long nowTicks)
    {
        int epoch = _clock.Epoch;
        if (epoch != _wEpoch)
        {
            _wEpoch = epoch;
            _wRateNominal = _clock.SampleRate;
            _wFrames = 0;
            _wJitter.Reset();
            _wTotal = 0;
            _wRate.Reset();
            _wRateValid = false;
            _wLastRawTicks = 0;
            _wPausedSeconds = 0;
        }
        int breaks = _clock.Breaks;
        if (breaks != _wBreaks)
        {
            // кадры пропали или пришли пачкой: прежняя шкала времени больше не верна
            _wBreaks = breaks;
            _wJitter.Reset();
            _wFrames = 0;
        }
        if (_clock.Intermittent && _wLastRawTicks != 0 && _wRateNominal > 0)
        {
            double since = (nowTicks - _wLastRawTicks) / (double)Stopwatch.Frequency;
            if (since > GapSeconds)
            {
                // Пауза источника: время шло, кадров не было. Для оценки частоты время «останавливается» на паузу
                // (минус ожидаемый интервал пакета), иначе минута тишины выглядела бы как источник на −100% медленнее.
                _wPausedSeconds += since - _wLastRawFrames / (double)_wRateNominal;
                _wJitter.Reset();
                _wFrames = 0;
            }
        }
        _wLastRawTicks = nowTicks;
        _wLastRawFrames = frames;
        // шкала ведётся и без читателя, чтобы к его подключению уже была настроена
        _wFrames += frames;
        long ideal = _wRateNominal > 0 ? _wJitter.Filter(nowTicks, _wFrames, _wRateNominal) : nowTicks;
        _wTotal += frames;
        _wRate.Add(nowTicks / (double)Stopwatch.Frequency - _wPausedSeconds, _wTotal);
        if (_wRate.Valid)
        {
            Volatile.Write(ref _wRateHz, _wRate.Rate);
            _wRateValid = true;
        }
        if (!_readerActive) return;
        Trace?.OnWrite(nowTicks, ideal, frames);

        Interlocked.Increment(ref _seq); // нечётный: идёт запись
        int written = _ring.Write(stereo, frames);
        _lastWriteTicks = ideal;
        _lastWriteFrames = frames;
        Interlocked.Increment(ref _seq);
        if (written < frames) Interlocked.Add(ref _droppedFrames, frames - written);
    }

    // ── читатель (поток выхода) ─────────────────────────────────

    /// <summary>Выход остановился: провод больше не принимает запись.</summary>
    public void Deactivate()
    {
        _active = false;
        _readerActive = false;
        _publicState = WireState.Idle;
    }

    /// <summary>
    /// Выдаёт frames кадров провода и добавляет их в mix (interleaved-стерео) с усилением провода.
    /// scratch — рабочий буфер не меньше frames·2. outPeriodSeconds — период выхода (0 — обычный).
    /// </summary>
    /// <returns>
    /// true — в этом блоке случился underrun, а цель провода понижена малыми периодами: выход с «Низкой задержкой»
    /// считает это своим сбоем для автоотката.
    /// </returns>
    public bool Render(Span<float> mix, Span<float> scratch, int frames, int outRate, long nowTicks, double outPeriodSeconds = 0)
    {
        int before = _underruns;
        RenderBlock(mix, scratch, frames, outRate, nowTicks, outPeriodSeconds);
        return _smallTarget && _underruns != before;
    }

    private void RenderBlock(Span<float> mix, Span<float> scratch, int frames, int outRate, long nowTicks, double outPeriodSeconds)
    {
        if (!_active || outRate != _outRate || outPeriodSeconds != _outPeriod)
        {
            _outRate = outRate;
            _outPeriod = outPeriodSeconds;
            _active = true;
            _epoch = -1;
            _gain.Init(outRate, _targetGain, _targetGain, !_enabled);
            _fadeFrames = Math.Max(1, (int)(outRate * FadeSeconds));
            _ctl.Reset();
            _rJitter.Reset();
            _rRate.Reset();
            _rFrames = 0;
            _readerActive = true;
        }
        long rawNow = nowTicks;
        _rRate.Add(nowTicks / (double)Stopwatch.Frequency, _rFrames);
        nowTicks = _rJitter.Filter(nowTicks, _rFrames, outRate);
        _rFrames += frames;
        _traceNow = rawNow;

        int epoch = _clock.Epoch;
        if (epoch != _epoch)
        {
            _epoch = epoch;
            OnSourceRestart(_clock.SampleRate);
        }

        int breaks = _clock.Breaks;
        if (breaks != _breaksSeen)
        {
            _breaksSeen = breaks;
            _lastBreakRead = _reads;
        }
        _reads++;

        if (_inRate == 0)
        {
            _state = WireState.Idle;
            _publicState = WireState.Idle;
            return;
        }
        if (_dryPending) ClassifyDry(nowTicks);

        float g = _targetGain;
        _gain.SetTargets(g, g, !_enabled);

        Span<float> buf = scratch[..(frames * 2)];
        bool produced = _state == WireState.Running
            ? ReadRunning(buf, frames, nowTicks)
            : TryStart(buf, frames, nowTicks) && ReadRunning(buf, frames, nowTicks);

        if (!produced)
        {
            _publicState = _state;
            return;
        }

        _gain.Apply(buf, frames);
        Peak.Update(buf, frames);
        for (int i = 0; i < buf.Length; i++) mix[i] += buf[i];
        _publicState = _state;
    }

    /// <summary>
    /// Кольцо прерывистого источника опустело: если источник молчит дольше GapSeconds — это пауза приложения,
    /// если вернулся раньше — настоящий underrun (пакет опоздал).
    /// </summary>
    private void ClassifyDry(long nowTicks)
    {
        long lw = Volatile.Read(ref _lastWriteTicks);
        bool resumed = lw != _dryLastWrite;
        double silent = ((resumed ? lw : nowTicks) - _dryLastWrite) / (double)Stopwatch.Frequency;
        if (silent > GapSeconds)
        {
            Interlocked.Increment(ref _gaps);
            _dryPending = false;
        }
        else if (resumed)
        {
            Interlocked.Increment(ref _underruns);
            _dryPending = false;
        }
    }

    private void OnSourceRestart(int inRate)
    {
        _ring.Flush();
        _state = WireState.Priming;
        _dryPending = false;
        if (inRate == 0)
        {
            _inRate = 0;
            return;
        }
        if (inRate != _inRate || _outRate != _rsOutRate || _rs is null)
        {
            // новая пара частот: новый ресемплер (аллокация только при перенастройке)
            _rs = Resampler.Create(_opt.Resampler, (double)inRate / _outRate, _opt.MaxBlockFrames);
            _rsOutRate = _outRate;
            _ctl.Reset();
        }
        else
        {
            _ctl.Restart();
        }
        _inRate = inRate;
        _ring.Limit = (int)(inRate * _opt.RingMs / 1000.0);
        double target = TargetSeconds(_opt, _clock.PeriodSeconds, _outPeriod);
        _targetFrames = _baseTargetFrames = target * inRate;
        _smallTarget = target < TargetSeconds(_opt, 0, 0);
        // Резерв на фейд съедает запас малой цели (при 5,3 мс остаётся 0,7 мс на опоздание пробуждения),
        // поэтому фейд — четверть цели, от 1 до 2 мс. При обычной цели 20 мс — 2 мс, как в v1.
        _fadeFrames = Math.Max(1, (int)(_outRate * Math.Clamp(target / 4, MinFadeSeconds, FadeSeconds)));
        _breaksAtStart = _clock.Breaks;
        _targetRaised = false;
        _overflowFrames = (int)(inRate * _opt.OverflowMs / 1000.0);
        _resyncSeconds = _opt.ResyncMs / 1000.0;
        _trackResyncSeconds = _opt.TrackResyncMs / 1000.0;
    }

    /// <summary>
    /// Цель заполнения кольца, с: TargetPeriods периодов самого медленного из двух устройств на концах провода,
    /// но не меньше MinTargetMs (v2, «Низкая задержка»). Период 0 — обычный (PeriodMs). При 10 мс с обеих сторон — 20 мс, как в v1.
    /// </summary>
    public static double TargetSeconds(EngineOptions opt, double sourcePeriod, double outputPeriod)
    {
        double normal = opt.PeriodMs / 1000.0;
        double slowest = Math.Max(sourcePeriod > 0 ? sourcePeriod : normal, outputPeriod > 0 ? outputPeriod : normal);
        return Math.Max(opt.TargetPeriods * slowest, opt.MinTargetMs / 1000.0);
    }

    private double Ratio => (double)_inRate / _outRate * (1 + _ctl.Epsilon);

    /// <summary>Заполнение кольца с поправкой на время с последней записи: непрерывная оценка без «пилы» блоков.</summary>
    private double VirtualFill(out int available, long nowTicks)
    {
        long ticks = 0;
        int lastFrames = 0;
        available = 0;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int s1 = Volatile.Read(ref _seq);
            available = _ring.Available;
            ticks = Volatile.Read(ref _lastWriteTicks);
            lastFrames = Volatile.Read(ref _lastWriteFrames);
            if ((s1 & 1) == 0 && Volatile.Read(ref _seq) == s1) break;
        }
        // обе шкалы уже без джиттера; запись могла случиться «позже» момента чтения — тогда поправка отрицательная
        double since = (nowTicks - ticks) / (double)Stopwatch.Frequency;
        // предел — два блока: следующий блок может опоздать больше чем на свою длину, а без предела
        // застой источника раздул бы оценку
        double extra = Math.Clamp(since * _inRate, -2.0 * lastFrames, 2.0 * lastFrames);
        return available + extra;
    }

    private bool TryStart(Span<float> buf, int frames, long nowTicks)
    {
        _state = WireState.Priming;
        _rs!.Reset();
        double virt = VirtualFill(out int avail, nowTicks);
        int need = _rs.InputNeeded(frames, (double)_inRate / _outRate * (1 + _ctl.Epsilon));
        if (avail < need + _targetFrames) return false;

        // Лишнее сверх цели выбрасываем сразу: стартуем из тишины с фейдом, так что потеря не слышна,
        // а регулятору не придётся минутами сгонять избыток на ±1000 ppm.
        int skip = (int)Math.Floor(virt - need - _targetFrames);
        skip = Math.Clamp(skip, 0, avail - need);
        if (skip > 0) _ring.Skip(skip);
        _fadeInLeft = _fadeFrames;
        _state = WireState.Running;
        return true;
    }

    private bool ReadRunning(Span<float> buf, int frames, long nowTicks)
    {
        var rs = _rs!;
        double ratio = Ratio;
        int avail = _ring.Available;
        int need = rs.InputNeeded(frames, ratio);
        // резерв на фейд: если кольцо кончится ровно на границе блока, следующему блоку нечего будет гасить
        int reserve = (int)Math.Ceiling(_fadeFrames * ratio) + 2;

        if (avail < need + reserve)
        {
            // Кольцо опустело: доиграть, что есть, с фейдом 2 мс, остальное — нули; потом заново накопить цель.
            int m = rs.MaxOutput(avail, ratio, frames);
            if (m > 0)
            {
                rs.Process(_ring, buf, m, ratio);
                ApplyFadeIn(buf, m);
                FadeOutTail(buf, m);
            }
            buf[(m * 2)..].Clear();
            if (_clock.Intermittent)
            {
                _dryPending = true;
                _dryLastWrite = Volatile.Read(ref _lastWriteTicks);
            }
            else
            {
                Interlocked.Increment(ref _underruns);
            }
            // Underrun сразу после разрыва у источника — это пропущенный пакет, а не случайность: цель на один пакет выше.
            if (_breaksSeen > _breaksAtStart && _reads - _lastBreakRead <= 5) RaiseTarget();
            _state = WireState.Priming;
            _publicState = WireState.Priming;
            return true;
        }

        rs.Process(_ring, buf, frames, ratio);
        ApplyFadeIn(buf, frames);

        double fill = VirtualFill(out int availAfter, nowTicks);
        if (availAfter > _overflowFrames)
        {
            // Кольцо переполнилось (выход стоял): выбросить старое до цели, с фейдом на стыке.
            FadeOutTail(buf, frames);
            _ring.Skip(availAfter - (int)_targetFrames);
            rs.Reset();
            _fadeInLeft = _fadeFrames;
            _ctl.Resynced();
            Interlocked.Increment(ref _overruns);
            fill = _targetFrames;
        }
        else
        {
            bool driftValid = _wRateValid && _rRate.Valid;
            double drift = 0;
            if (driftValid)
            {
                double r = _rRate.Rate;
                drift = r > 0 ? Volatile.Read(ref _wRateHz) / _inRate / (r / _outRate) - 1 : 0;
                Volatile.Write(ref _driftPpm, drift * 1e6);
                // Источник медленнее и отдаёт пакеты по таймеру: недостача приходит пропуском целого пакета.
                // С целью в два периода пропуск съедает весь запас, поэтому цель поднимается на один пакет источника.
                if (!_targetRaised && drift < -100e-6 && _clock.Breaks > _breaksAtStart + 1)
                {
                    RaiseTarget();
                    _ctl.Restart();
                }
            }
            _ctl.Update(fill, _targetFrames, _inRate, (double)frames / _outRate, driftValid, drift);
            Trace?.OnRead(_traceNow, nowTicks, frames, availAfter, fill, _ctl.Epsilon);
            if (_ctl.ErrorSeconds > (_ctl.Tracking ? _trackResyncSeconds : _resyncSeconds))
            {
                // Избыток пришёл скачком (источник отдал накопленное). На ±1000 ppm его сгоняли бы 10+ секунд
                // с лишней задержкой всё это время, поэтому выбрасываем сразу — так же, как при переполнении.
                int skip = (int)(fill - _targetFrames);
                skip = Math.Clamp(skip, 0, Math.Max(0, availAfter - (int)(_targetFrames / 2)));
                FadeOutTail(buf, frames);
                _ring.Skip(skip);
                rs.Reset();
                _fadeInLeft = _fadeFrames;
                _ctl.Resynced();
                Interlocked.Increment(ref _resyncs);
                fill -= skip;
            }
        }

        Volatile.Write(ref _fillMs, _ctl.SmoothedFill * 1000.0 / _inRate);
        Volatile.Write(ref _epsPpm, _ctl.Epsilon * 1e6);
        _tracking = _ctl.Tracking;
        Volatile.Write(ref _latencyMs, (fill + rs.DelayFrames) * 1000.0 / _inRate);
        return true;
    }

    private void RaiseTarget()
    {
        if (_targetRaised) return;
        _targetRaised = true;
        _targetFrames = _baseTargetFrames + Volatile.Read(ref _lastWriteFrames);
    }

    private void ApplyFadeIn(Span<float> buf, int frames)
    {
        if (_fadeInLeft <= 0) return;
        float total = _fadeFrames;
        for (int i = 0; i < frames && _fadeInLeft > 0; i++)
        {
            float k = 1f - _fadeInLeft / total;
            buf[2 * i] *= k;
            buf[2 * i + 1] *= k;
            _fadeInLeft--;
        }
    }

    private void FadeOutTail(Span<float> buf, int frames)
    {
        int n = Math.Min(frames, _fadeFrames);
        int start = frames - n;
        for (int i = 0; i < n; i++)
        {
            float k = 1f - (i + 1f) / n;
            buf[2 * (start + i)] *= k;
            buf[2 * (start + i) + 1] *= k;
        }
    }
}
