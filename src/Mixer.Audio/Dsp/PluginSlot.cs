using System.Diagnostics;
using Mixer.Plugins.Host;

namespace Mixer.Audio.Dsp;

/// <summary>Что видит пользователь у слота. Сигнал меняет только <see cref="Running"/>, в остальных он идёт как есть.</summary>
public enum PluginSlotState
{
    Loading,
    Running,
    /// <summary>Выключен пользователем (обход).</summary>
    Off,
    /// <summary>Сбой: хост упал или завис, плагин не успевает, выдал NaN, не загрузился.</summary>
    Failed,
    /// <summary>Плагина из схемы нет на этой машине.</summary>
    Missing,
}

/// <summary>Почему слот в сбое (<see cref="PluginSlotState.Failed"/>) или не загружен.</summary>
public enum PluginFault
{
    None,
    /// <summary>Хост упал: через 2 с перезапуск с последним состоянием.</summary>
    Crashed,
    /// <summary>Хост не отвечал и был завершён: дальше как при падении.</summary>
    Hung,
    /// <summary>Три падения за 5 минут: ждёт «Перезапустить».</summary>
    CrashedRepeatedly,
    /// <summary>Больше 10 просрочек за 10 с: выключен, чтобы не дробить звук.</summary>
    TooSlow,
    /// <summary>Выдал NaN или бесконечность.</summary>
    BadOutput,
    LoadFailed,
    /// <summary>Плагина нет на этой машине.</summary>
    NotFound,
    /// <summary>Больше 4 слотов на узле или 16 на схеме.</summary>
    Limit,
}

/// <summary>
/// Слот плагина — шаг цепочки обработки (VST, «Слот в аудиопотоке»). Аудиопоток узла отдаёт блок хосту через
/// <see cref="AudioLink"/> и ждёт ответ до дедлайна; без аллокаций, блокировок и журнала. Не успел, хост в обходе,
/// упал — блок идёт сухим. Сухой сигнал всегда задержан на задержку плагина, поэтому переходы «сухой ↔ обработанный»
/// не сдвигают звук во времени: включение и выключение — кроссфейд 10 мс, возврат после просрочки — 2 мс, а резкий
/// переход на сухой (обработанного блока нет) сглаживается за 2 мс смещением от последнего отсчёта.
/// Управляет слотом (подключает хост, решает о сбое) управляющий поток: здесь только звук и счётчики.
/// </summary>
public sealed class PluginSlot
{
    public const double FadeSeconds = 0.010;
    public const double MissFadeSeconds = 0.002;
    /// <summary>+12 dBFS: выше — жёсткое ограничение на выходе слота.</summary>
    public const float LoudLimit = 3.9810717f;

    private readonly float[] _inL, _inR, _outL, _outR, _dry;
    private AudioLink? _link;
    private volatile DelayLine? _delay;
    private volatile bool _wet;
    private volatile int _hostRate;
    private volatile bool _halted;
    private volatile bool _post;
    private int _busy;

    // ── только аудиопоток ──
    private float _mix;
    private bool _recovering;
    private float _lastL, _lastR;
    private DelayLine? _lastDelay;

    // ── аудиопоток → управляющий ──
    private int _misses, _badOutput, _loud, _seenRate;
    private long _processTicks, _processFrames;

    public PluginSlot(string id, int maxFrames)
    {
        Id = id;
        MaxFrames = maxFrames;
        _inL = new float[maxFrames];
        _inR = new float[maxFrames];
        _outL = new float[maxFrames];
        _outR = new float[maxFrames];
        _dry = new float[maxFrames * 2];
    }

    public string Id { get; }
    public int MaxFrames { get; }
    /// <summary>Группа «после» встроенной обработки (иначе «до»).</summary>
    public bool Post { get => _post; set => _post = value; }
    /// <summary>Индикатор на выходе слота.</summary>
    public PeakMeter Meter { get; } = new();

    public volatile PluginSlotState State = PluginSlotState.Loading;
    public volatile PluginFault Fault;
    /// <summary>Подробности сбоя (текст хоста, английский — для журнала и подсказки).</summary>
    public volatile string? Detail;

    /// <summary>Задержка плагина (с «ровными блоками»), кадры: на столько же задержан сухой сигнал.</summary>
    public int LatencySamples => _delay?.Length ?? 0;
    /// <summary>Частота, на которой слот считается сейчас (0 — ещё не звал аудиопоток).</summary>
    public int SeenRate => Volatile.Read(ref _seenRate);
    public int HostRate => _hostRate;
    public bool IsAttached => Volatile.Read(ref _link) is not null;

    // ── управляющий поток ──

    /// <summary>Хост готов: блоки пойдут ему, как только аудиопоток увидит ту же частоту.</summary>
    public void Attach(AudioLink link, int hostRate, int latencySamples)
    {
        SetLatency(latencySamples);
        _hostRate = hostRate;
        _halted = false;
        Interlocked.Exchange(ref _link, link);
    }

    /// <summary>
    /// Отключить хост: после возврата аудиопоток его не трогает, секцию можно освобождать. Ждёт текущий блок
    /// (он ограничен дедлайном). false — блок не закончился за timeoutMs.
    /// </summary>
    public bool Detach(int timeoutMs = 1000)
    {
        Interlocked.Exchange(ref _link, null);
        long until = Environment.TickCount64 + timeoutMs;
        while (Volatile.Read(ref _busy) != 0)
        {
            if (Environment.TickCount64 > until) return false;
            Thread.Sleep(1);
        }
        return true;
    }

    /// <summary>Нужен обработанный сигнал (работает и включён); false — плавный переход в обход.</summary>
    public bool Wet { get => _wet; set => _wet = value; }

    public void SetLatency(int samples)
    {
        if (samples == LatencySamples) return;
        _delay = samples > 0 ? new DelayLine(samples) : null;
    }

    public void SetHostRate(int rate) => _hostRate = rate;

    /// <summary>Просроченных блоков с прошлого опроса.</summary>
    public int TakeMisses() => Interlocked.Exchange(ref _misses, 0);
    /// <summary>Плагин выдал NaN или бесконечность: блоки ему больше не идут до нового <see cref="Attach"/>.</summary>
    public bool TakeBadOutput() => Interlocked.Exchange(ref _badOutput, 0) != 0;
    /// <summary>Блоков выше +12 dBFS с прошлого опроса.</summary>
    public int TakeLoud() => Interlocked.Exchange(ref _loud, 0);
    /// <summary>Время process в хосте (тики QPC) и сколько кадров за это время обработано.</summary>
    public (long Ticks, long Frames) TakeLoad() =>
        (Interlocked.Exchange(ref _processTicks, 0), Interlocked.Exchange(ref _processFrames, 0));

    // ── аудиопоток ──

    /// <summary>Обработать блок interleaved-стерео на месте. Другой поток в это время — блок проходит как есть.</summary>
    internal void Process(Span<float> io, int frames, int rate, long deadlineQpc)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            Run(io, frames, rate, deadlineQpc);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private void Run(Span<float> io, int frames, int rate, long deadlineQpc)
    {
        Volatile.Write(ref _seenRate, rate);
        if (frames > MaxFrames || frames <= 0)
        {
            Meter.Update(io, frames);
            return;
        }
        int n2 = frames * 2;
        var dry = _dry.AsSpan(0, n2);
        io[..n2].CopyTo(dry);
        var delay = _delay;
        delay?.Process(dry);

        bool wanted = _wet;
        bool wet = false;
        var link = Volatile.Read(ref _link);
        // и в обходе плагин получает звук: иначе при включении из его линии задержки и хвостов прозвучало бы
        // то, что застряло там в момент выключения
        if (link is not null && !_halted && rate == _hostRate)
            wet = SendBlock(link, io, frames, deadlineQpc);

        if (wet)
        {
            float target = wanted ? 1f : 0f;
            if (_mix == 1f && target == 1f)
            {
                for (int i = 0, j = 0; i < frames; i++, j += 2)
                {
                    io[j] = _outL[i];
                    io[j + 1] = _outR[i];
                }
            }
            else
            {
                float step = 1f / Math.Max(1, (int)(rate * (_recovering ? MissFadeSeconds : FadeSeconds)));
                float m = _mix;
                for (int i = 0, j = 0; i < frames; i++, j += 2)
                {
                    m = target > m ? MathF.Min(target, m + step) : MathF.Max(target, m - step);
                    float s = m * m * (3 - 2 * m);
                    io[j] = dry[j] + (_outL[i] - dry[j]) * s;
                    io[j + 1] = dry[j + 1] + (_outR[i] - dry[j + 1]) * s;
                }
                _mix = m;
                if (m == target) _recovering = false;
            }
        }
        else
        {
            dry.CopyTo(io);
            // обработанного блока нет, а только что звучал он (или сменилась задержка): ступеньку сглаживаем
            if (_mix > 0 || !ReferenceEquals(delay, _lastDelay)) Declick(io, frames, rate);
            if (_mix > 0 && wanted) _recovering = true;
            _mix = 0;
        }
        _lastDelay = delay;
        _lastL = io[n2 - 2];
        _lastR = io[n2 - 1];
        Meter.Update(io, frames);
    }

    /// <returns>Обработанный блок в _outL/_outR годен.</returns>
    private bool SendBlock(AudioLink link, ReadOnlySpan<float> io, int frames, long deadlineQpc)
    {
        for (int i = 0, j = 0; i < frames; i++, j += 2)
        {
            _inL[i] = io[j];
            _inR[i] = io[j + 1];
        }
        var t = new BlockTiming();
        var outcome = link.Process(_inL.AsSpan(0, frames), _inR.AsSpan(0, frames), _outL.AsSpan(0, frames), _outR.AsSpan(0, frames),
            deadlineQpc, ref t);
        if (outcome != BlockOutcome.Processed)
        {
            if (outcome == BlockOutcome.Missed) Interlocked.Increment(ref _misses);
            return false;
        }
        Interlocked.Add(ref _processTicks, t.ProcessTicks);
        Interlocked.Add(ref _processFrames, frames);

        float peak = 0;
        for (int i = 0; i < frames; i++)
        {
            float l = _outL[i], r = _outR[i];
            if (!float.IsFinite(l) || !float.IsFinite(r))
            {
                // один такой блок навсегда испортил бы фильтры дальше по цепочке
                _halted = true;
                Volatile.Write(ref _badOutput, 1);
                return false;
            }
            peak = MathF.Max(peak, MathF.Max(MathF.Abs(l), MathF.Abs(r)));
        }
        if (peak > LoudLimit)
        {
            for (int i = 0; i < frames; i++)
            {
                _outL[i] = Math.Clamp(_outL[i], -LoudLimit, LoudLimit);
                _outR[i] = Math.Clamp(_outR[i], -LoudLimit, LoudLimit);
            }
            Interlocked.Increment(ref _loud);
        }
        return true;
    }

    /// <summary>Сгладить ступеньку между прошлым отсчётом и началом блока: смещение уходит в ноль за 2 мс.</summary>
    private void Declick(Span<float> io, int frames, int rate)
    {
        int len = Math.Min(frames, Math.Max(1, (int)(rate * MissFadeSeconds)));
        float dl = _lastL - io[0], dr = _lastR - io[1];
        for (int i = 0, j = 0; i < len; i++, j += 2)
        {
            float k = 1f - (i + 1f) / len;
            float s = k * k * (3 - 2 * k);
            io[j] += dl * s;
            io[j + 1] += dr * s;
        }
    }

    /// <summary>Задержка сухого сигнала на задержку плагина: кольцо interleaved-стерео.</summary>
    private sealed class DelayLine(int frames)
    {
        private readonly float[] _buf = new float[frames * 2];
        private int _pos;

        public int Length => frames;

        public void Process(Span<float> io)
        {
            var buf = _buf;
            int pos = _pos;
            for (int j = 0; j < io.Length; j++)
            {
                float v = buf[pos];
                buf[pos] = io[j];
                io[j] = v;
                if (++pos == buf.Length) pos = 0;
            }
            _pos = pos;
        }
    }

    /// <summary>Дедлайн блока: старт плюс 40% периода узла (VST, «Бюджет времени на блок»).</summary>
    public static long Deadline(long blockStartQpc, double periodSeconds) =>
        blockStartQpc + (long)(0.4 * periodSeconds * Stopwatch.Frequency);
}
