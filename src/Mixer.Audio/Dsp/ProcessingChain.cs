namespace Mixer.Audio.Dsp;

/// <summary>Линейная рампа параметра по кадрам: значение двигается к цели ровно за заданное число кадров.</summary>
internal struct ParamRamp
{
    public float Cur;
    public float Target;
    private float _step;
    private int _left;

    public readonly bool Settled => _left == 0;

    public void Jump(float v)
    {
        Cur = Target = v;
        _left = 0;
    }

    public void Set(float target, int frames)
    {
        if (target == Target && (_left > 0 || Cur == target)) return;
        Target = target;
        if (frames <= 0 || Cur == target)
        {
            Jump(target);
            return;
        }
        _left = frames;
        _step = (target - Cur) / frames;
    }

    public void Advance(int frames)
    {
        if (_left == 0) return;
        if (frames >= _left)
        {
            Cur = Target;
            _left = 0;
            return;
        }
        Cur += _step * frames;
        _left -= frames;
    }
}

/// <summary>
/// Индикаторы цепочки для интерфейса: уровень до и после обработки, подавление компрессора, огибающая гейта.
/// Аудиопоток пишет максимумы без блокировок, интерфейс забирает их с обнулением.
/// </summary>
public sealed class ChainMeters
{
    private int _reduction;
    private int _gateLevel;
    private volatile int _gate = -1;

    public PeakMeter In { get; } = new();
    public PeakMeter Out { get; } = new();

    /// <summary>Гейт сейчас открыт (null — гейт выключен).</summary>
    public bool? GateOpen => _gate < 0 ? null : _gate == 1;

    internal void SetGate(bool? open) => _gate = open is null ? -1 : open.Value ? 1 : 0;
    internal void Reduction(float db) => Max(ref _reduction, db);
    internal void GateLevel(float lin) => Max(ref _gateLevel, lin);

    /// <summary>Наибольшее подавление компрессора с прошлого опроса, дБ (положительное).</summary>
    public float TakeReduction() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _reduction, 0));

    /// <summary>Наибольшая огибающая детектора гейта с прошлого опроса (линейная).</summary>
    public float TakeGateLevel() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _gateLevel, 0));

    private static void Max(ref int slot, float value)
    {
        if (!(value > 0)) return;
        int bits = BitConverter.SingleToInt32Bits(value);
        int cur = Volatile.Read(ref slot);
        while (bits > cur)
        {
            int seen = Interlocked.CompareExchange(ref slot, bits, cur);
            if (seen == cur) return;
            cur = seen;
        }
    }
}

/// <summary>
/// Цепочка обработки узла: гейт → EQ → компрессор. Работает в аудиопотоке узла, без блокировок и аллокаций.
/// Параметры приходят неизменяемым <see cref="ChainSettings"/> (<see cref="Set"/>); поток подхватывает новую ссылку
/// и за <see cref="RampSeconds"/> плавно переводит каждый параметр к новому значению: частоты EQ — по логарифму,
/// усиления — в дБ, коэффициенты биквадов пересчитываются каждые <see cref="Chunk"/> кадров, пока идёт переход.
/// Включение и выключение блока — кроссфейд с сухим сигналом той же длины. Выключенный блок не считается.
/// Задержки цепочка не добавляет (просмотра вперёд нет).
/// </summary>
public sealed class ProcessingChain
{
    public const double RampSeconds = 0.010;
    /// <summary>Шаг пересчёта медленных параметров, кадры.</summary>
    public const int Chunk = 16;
    /// <summary>Спад детектора гейта: огибающая не должна проваливаться между периодами низкой частоты.</summary>
    public const double GateDetectorReleaseSeconds = 0.030;

    private const float Log2To10 = 0.30103f; // log10(2)
    private const float DbToLn = 0.115129255f; // ln(10)/20

    private volatile ChainSettings? _pending;
    private ChainSettings? _applied;
    private int _rate;
    private int _rampFrames;
    private bool _idle = true;

    // ── гейт ──
    private ParamRamp _gMix, _gThrDb, _gDepthDb;
    private float _gAtt, _gRel, _gDetRel;
    private int _gHold;
    private float _gEnv, _gGain = 1;
    private int _gHoldLeft;
    private bool _gOpen;

    // ── EQ ──
    private ParamRamp _eMix, _hpMix, _hpLogF;
    private StereoBiquad _hp;
    private readonly Band[] _bands = new Band[EqSettings.BandCount];

    // ── компрессор ──
    private ParamRamp _cMix, _cThr, _cRatio, _cKnee, _cMakeup;
    private float _cAtt, _cRel;
    private float _cGr, _cPeak;

    public ChainMeters Meters { get; } = new();

    /// <summary>Слоты плагинов: «до» и «после» встроенной обработки, по порядку.</summary>
    private sealed record Slots(PluginSlot[] Pre, PluginSlot[] Post)
    {
        public static readonly Slots None = new([], []);
        public bool Any => Pre.Length + Post.Length > 0;
    }

    private volatile Slots _slots = Slots.None;

    /// <summary>Управляющий поток: новые настройки (null — без обработки).</summary>
    public void Set(ChainSettings? settings) => _pending = settings;

    public ChainSettings? Settings => _pending;

    /// <summary>Управляющий поток: слоты плагинов по порядку (группа — <see cref="PluginSlot.Post"/>).</summary>
    public void SetPlugins(IReadOnlyList<PluginSlot>? slots) =>
        _slots = slots is not { Count: > 0 } ? Slots.None : new(slots.Where(s => !s.Post).ToArray(), slots.Where(s => s.Post).ToArray());

    public IReadOnlyList<PluginSlot> Plugins => [.. _slots.Pre, .. _slots.Post];

    /// <summary>Задержка всех плагинов цепочки, кадры.</summary>
    public int LatencySamples
    {
        get
        {
            var s = _slots;
            int sum = 0;
            foreach (var p in s.Pre) sum += p.LatencySamples;
            foreach (var p in s.Post) sum += p.LatencySamples;
            return sum;
        }
    }

    /// <summary>
    /// Аудиопоток: обработать блок interleaved-стерео на месте: плагины «до» → гейт → EQ → компрессор → плагины «после».
    /// <paramref name="deadlineQpc"/> — дедлайн плагинов (<see cref="PluginSlot.Deadline"/>); 0 — 40% длины блока от сейчас.
    /// </summary>
    public void Process(Span<float> stereo, int frames, int sampleRate, long deadlineQpc = 0)
    {
        var p = _pending;
        if (sampleRate != _rate)
        {
            Init(sampleRate, p);
        }
        else if (!ReferenceEquals(p, _applied))
        {
            Retarget(p, _rampFrames);
        }
        var slots = _slots;
        if (_idle && !slots.Any) return;
        if (slots.Any && deadlineQpc == 0)
            deadlineQpc = PluginSlot.Deadline(System.Diagnostics.Stopwatch.GetTimestamp(), frames / (double)sampleRate);

        Meters.In.Update(stereo, frames);
        foreach (var s in slots.Pre) s.Process(stereo, frames, sampleRate, deadlineQpc);
        if (!_idle)
        {
            Span<float> dry = stackalloc float[Chunk * 2];
            for (int off = 0; off < frames; off += Chunk)
            {
                int n = Math.Min(Chunk, frames - off);
                var block = stereo.Slice(off * 2, n * 2);
                if (_gMix.Cur > 0 || _gMix.Target > 0) RunGate(block, dry, n);
                if (_eMix.Cur > 0 || _eMix.Target > 0) RunEq(block, dry, n);
                if (_cMix.Cur > 0 || _cMix.Target > 0) RunComp(block, dry, n);
            }
            UpdateIdle();
        }
        foreach (var s in slots.Post) s.Process(stereo, frames, sampleRate, deadlineQpc);
        Meters.Out.Update(stereo, frames);
    }

    private void UpdateIdle()
    {
        bool gate = _gMix.Cur > 0 || _gMix.Target > 0;
        _idle = !gate && !(_eMix.Cur > 0 || _eMix.Target > 0) && !(_cMix.Cur > 0 || _cMix.Target > 0);
        Meters.SetGate(gate ? _gOpen : null);
    }

    // ── настройки ──

    private void Init(int rate, ChainSettings? p)
    {
        _rate = rate;
        _rampFrames = Math.Max(1, (int)(rate * RampSeconds));
        _gDetRel = Coef(GateDetectorReleaseSeconds);
        _gEnv = 0;
        _gGain = 1;
        _gHoldLeft = 0;
        _gOpen = false;
        _hp.Reset();
        for (int i = 0; i < _bands.Length; i++) _bands[i].Reset();
        _cGr = _cPeak = 0;
        // новая частота — новые коэффициенты сразу, без перехода: поток только открылся
        Retarget(p, 0);
        UpdateEqCoeffs(force: true);
    }

    private void Retarget(ChainSettings? p, int ramp)
    {
        _applied = p;
        var s = p ?? Empty;

        var g = s.Gate;
        _gMix.Set(g.Enabled ? 1 : 0, ramp);
        _gThrDb.Set(g.ThresholdDb, ramp);
        _gDepthDb.Set(g.DepthDb, ramp);
        _gAtt = 1 - Coef(g.AttackMs / 1000.0);
        _gRel = 1 - Coef(g.ReleaseMs / 1000.0);
        _gHold = (int)(g.HoldMs / 1000.0 * _rate);

        var e = s.Eq;
        _eMix.Set(e.Enabled ? 1 : 0, ramp);
        _hpMix.Set(e.Enabled && e.HighPass ? 1 : 0, ramp);
        _hpLogF.Set(MathF.Log2(e.HighPassHz), ramp);
        var bands = e.Bands;
        for (int i = 0; i < _bands.Length; i++)
            _bands[i].Retarget(i < bands.Count ? bands[i] : Off, ramp);

        var c = s.Comp;
        _cMix.Set(c.Enabled ? 1 : 0, ramp);
        _cThr.Set(c.ThresholdDb, ramp);
        _cRatio.Set(c.Ratio, ramp);
        _cKnee.Set(c.KneeDb, ramp);
        _cMakeup.Set(c.MakeupDb, ramp);
        _cAtt = Coef(c.AttackMs / 1000.0);
        _cRel = Coef(c.ReleaseMs / 1000.0);
        UpdateIdle();
    }

    private static readonly ChainSettings Empty = new();
    private static readonly EqBand Off = new();

    /// <summary>Коэффициент одного полюса с постоянной времени seconds.</summary>
    private float Coef(double seconds) => (float)Math.Exp(-1.0 / (Math.Max(seconds, 1e-5) * _rate));

    // ── гейт ──

    private void RunGate(Span<float> block, Span<float> dry, int n)
    {
        float m0 = _gMix.Cur;
        _gMix.Advance(n);
        _gThrDb.Advance(n);
        _gDepthDb.Advance(n);
        float m1 = _gMix.Cur;
        bool fade = m0 < 1 || m1 < 1;
        if (fade) block.CopyTo(dry);

        float thr = MathF.Pow(10, _gThrDb.Cur / 20), depth = MathF.Pow(10, _gDepthDb.Cur / 20);
        float env = _gEnv, g = _gGain, att = _gAtt, rel = _gRel, det = _gDetRel, top = 0;
        int hold = _gHoldLeft;
        bool open = _gOpen;
        for (int i = 0, j = 0; i < n; i++, j += 2)
        {
            float l = block[j], r = block[j + 1];
            float peak = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            env = peak > env ? peak : env * det;
            if (env > top) top = env;
            if (env >= thr)
            {
                open = true;
                hold = _gHold;
            }
            else if (hold > 0)
            {
                hold--;
            }
            else
            {
                open = false;
            }
            float target = open ? 1f : depth;
            g += (target - g) * (target > g ? att : rel);
            block[j] = l * g;
            block[j + 1] = r * g;
        }
        _gEnv = env < 1e-20f ? 0 : env;
        _gGain = g;
        _gHoldLeft = hold;
        _gOpen = open;
        Meters.GateLevel(top);

        if (fade) Crossfade(block, dry, n, m0, m1);
        if (m1 == 0 && _gMix.Target == 0)
        {
            // блок выключен до конца: следующее включение — с чистого состояния
            _gEnv = 0;
            _gGain = 1;
            _gHoldLeft = 0;
            _gOpen = false;
        }
    }

    // ── EQ ──

    private void RunEq(Span<float> block, Span<float> dry, int n)
    {
        float m0 = _eMix.Cur;
        _eMix.Advance(n);
        float m1 = _eMix.Cur;
        bool fade = m0 < 1 || m1 < 1;
        if (fade) block.CopyTo(dry);

        UpdateEqCoeffs(force: false, n);

        // ФВЧ: включение и выключение — кроссфейд, его нельзя «увести в единицу» параметром
        if (_hpMix.Cur > 0 || _hpMix.Target > 0)
        {
            float h0 = _hpMix.Cur;
            _hpMix.Advance(n);
            float h1 = _hpMix.Cur;
            if (h0 < 1 || h1 < 1)
            {
                Span<float> pre = stackalloc float[Chunk * 2];
                block.CopyTo(pre);
                _hp.Process(block, n);
                Crossfade(block, pre, n, h0, h1);
            }
            else
            {
                _hp.Process(block, n);
            }
            if (h1 == 0 && _hpMix.Target == 0) _hp.Reset();
        }
        for (int i = 0; i < _bands.Length; i++)
            if (_bands[i].Active) _bands[i].Filter.Process(block, n);

        if (fade) Crossfade(block, dry, n, m0, m1);
        if (m1 == 0 && _eMix.Target == 0)
        {
            _hp.Reset();
            for (int i = 0; i < _bands.Length; i++) _bands[i].Filter.Reset();
        }
    }

    /// <summary>Продвинуть переходы полос и пересчитать коэффициенты у тех, что двигаются.</summary>
    private void UpdateEqCoeffs(bool force, int frames = 0)
    {
        bool hpMoving = !_hpLogF.Settled;
        _hpLogF.Advance(frames);
        if (force || hpMoving)
            _hp.C = BiquadCoeffs.Design(BiquadKind.HighPass, MathF.Pow(2, _hpLogF.Cur), 0, 0.70710678, _rate);
        for (int i = 0; i < _bands.Length; i++) _bands[i].Advance(frames, _rate, force);
    }

    /// <summary>
    /// Полоса EQ в аудиопотоке. Смена типа (пик ↔ полка, включение, выключение) идёт через 0 дБ: пик и полки
    /// с нулевым усилением — точная единица, поэтому тип меняется без щелчка.
    /// </summary>
    private struct Band
    {
        public StereoBiquad Filter;
        private EqBandType _type, _tType;
        private float _tGain, _tLogF, _tLogQ;
        private ParamRamp _gain, _logF, _logQ;
        private int _ramp;
        /// <summary>
        /// Сколько ещё кадров держать полосу на 0 дБ перед сменой типа или обходом: хвост прежней формы фильтра
        /// должен затухнуть, иначе смена коэффициентов (или обход) оборвёт его ступенькой.
        /// </summary>
        private int _settle;

        public readonly bool Active => _type != EqBandType.Off;

        public void Reset()
        {
            Filter.Reset();
            Filter.C = BiquadCoeffs.Unity;
        }

        public void Retarget(EqBand b, int ramp)
        {
            _ramp = ramp;
            var pending = _tType;
            _tType = b.Type;
            _tGain = b.Type == EqBandType.Off ? 0 : b.GainDb;
            _tLogF = MathF.Log2(b.FreqHz);
            _tLogQ = MathF.Log2(b.Q);
            if (ramp == 0)
            {
                // без перехода (поток только открылся)
                _type = _tType;
                _gain.Jump(_tGain);
                _logF.Jump(_tLogF);
                _logQ.Jump(_tLogQ);
                return;
            }
            if (_type == EqBandType.Off && _tType != EqBandType.Off)
            {
                // из «выкл»: единичный фильтр нужного типа на нужной частоте, затем усиление растёт
                _type = _tType;
                _gain.Jump(0);
                _logF.Jump(_tLogF);
                _logQ.Jump(_tLogQ);
                Filter.Reset();
            }
            if (_type == _tType)
            {
                _gain.Set(_tGain, ramp);
                _logF.Set(_tLogF, ramp);
                _logQ.Set(_tLogQ, ramp);
            }
            else
            {
                _gain.Set(0, ramp); // сначала в 0 дБ прежним типом
                if (pending != _tType) _settle = 2 * ramp;
            }
        }

        public void Advance(int frames, int rate, bool force)
        {
            if (_type == EqBandType.Off) return;
            bool moving = !_gain.Settled || !_logF.Settled || !_logQ.Settled;
            _gain.Advance(frames);
            _logF.Advance(frames);
            _logQ.Advance(frames);
            if (_type != _tType && _gain.Settled && _gain.Cur == 0 && (_settle -= frames) <= 0)
            {
                _settle = 0;
                _type = _tType;
                if (_type == EqBandType.Off)
                {
                    Reset();
                    return;
                }
                _logF.Jump(_tLogF);
                _logQ.Jump(_tLogQ);
                _gain.Set(_tGain, _ramp);
                moving = true;
            }
            if (!moving && !force) return;
            var kind = _type switch
            {
                EqBandType.LowShelf => BiquadKind.LowShelf,
                EqBandType.HighShelf => BiquadKind.HighShelf,
                _ => BiquadKind.Peak,
            };
            Filter.C = BiquadCoeffs.Design(kind, MathF.Pow(2, _logF.Cur), _gain.Cur, MathF.Pow(2, _logQ.Cur), rate);
        }
    }

    // ── компрессор ──

    private void RunComp(Span<float> block, Span<float> dry, int n)
    {
        float m0 = _cMix.Cur;
        _cMix.Advance(n);
        float m1 = _cMix.Cur;
        bool fade = m0 < 1 || m1 < 1;
        if (fade) block.CopyTo(dry);

        // пороговые параметры — линейно внутри блока, чтобы поворот ручки не давал ступенек
        float thr0 = _cThr.Cur, ratio0 = _cRatio.Cur, knee0 = _cKnee.Cur, mk0 = _cMakeup.Cur;
        _cThr.Advance(n);
        _cRatio.Advance(n);
        _cKnee.Advance(n);
        _cMakeup.Advance(n);
        float dThr = (_cThr.Cur - thr0) / n, dRatio = (_cRatio.Cur - ratio0) / n, dKnee = (_cKnee.Cur - knee0) / n, dMk = (_cMakeup.Cur - mk0) / n;
        bool still = dThr == 0 && dRatio == 0 && dKnee == 0 && dMk == 0;
        float mkLin = MathF.Exp(mk0 * DbToLn);

        // Развязанный пиковый детектор (Giannoulis, Massberg, Reiss, 2012): пик подавления держится со спадом,
        // атака сглаживает уже его. Иначе между пиками синуса подавление успевало бы отпускать и не доходило
        // до статической кривой.
        float gr = _cGr, pk = _cPeak, att = _cAtt, rel = _cRel, top = 0;
        float thr = thr0, ratio = ratio0, knee = knee0, mk = mk0;
        for (int i = 0, j = 0; i < n; i++, j += 2)
        {
            if (!still)
            {
                thr += dThr;
                ratio += dRatio;
                knee += dKnee;
                mk += dMk;
            }
            float l = block[j], r = block[j + 1];
            float peak = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            float target = 0;
            if (peak > 1e-6f)
            {
                float x = MathF.Log2(peak) * (20 * Log2To10);
                float over = x - thr;
                if (2 * over > knee)
                    target = over * (1 - 1 / ratio);
                else if (knee > 0 && 2 * over > -knee)
                {
                    float t = over + knee / 2;
                    target = (1 - 1 / ratio) * t * t / (2 * knee);
                }
            }
            pk = MathF.Max(target, rel * pk + (1 - rel) * target);
            gr = att * gr + (1 - att) * pk;
            if (gr < 1e-5f) gr = 0;
            if (gr > top) top = gr;
            float g = gr == 0 && still ? mkLin : MathF.Exp((mk - gr) * DbToLn);
            block[j] = l * g;
            block[j + 1] = r * g;
        }
        _cGr = gr;
        _cPeak = pk < 1e-5f ? 0 : pk;
        Meters.Reduction(top * Math.Min(m1, 1));

        if (fade) Crossfade(block, dry, n, m0, m1);
        if (m1 == 0 && _cMix.Target == 0) _cGr = _cPeak = 0;
    }

    /// <summary>
    /// block = dry + (block − dry) · S(k), k идёт от m0 к m1 по кадрам. S — гладкая ступенька 3k² − 2k³: у линейного
    /// кроссфейда излом на концах, и при большой разнице «сухого» и обработанного он слышен как мягкий щелчок.
    /// </summary>
    private static void Crossfade(Span<float> block, ReadOnlySpan<float> dry, int n, float m0, float m1)
    {
        float step = (m1 - m0) / n;
        float k = m0;
        for (int i = 0, j = 0; i < n; i++, j += 2)
        {
            k += step;
            float s = k * k * (3 - 2 * k);
            block[j] = dry[j] + (block[j] - dry[j]) * s;
            block[j + 1] = dry[j + 1] + (block[j + 1] - dry[j + 1]) * s;
        }
    }
}
