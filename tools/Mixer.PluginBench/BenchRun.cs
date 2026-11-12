using System.Diagnostics;

namespace Mixer.PluginBench;

/// <summary>
/// Один узел с плагином: свой поток с MMCSS «Pro Audio», как поток устройства в движке, будится
/// таймером высокого разрешения раз в период и гонит блок через хост с дедлайном 40% периода.
/// </summary>
internal sealed unsafe class BenchRun
{
    private readonly HostChannel _channel;
    private readonly TimeSpan _duration;
    private readonly CancellationToken _cancel;
    private readonly double _periodTicks;
    private readonly long _deadlineTicks;
    private readonly double _gateUs;
    private Thread? _thread;

    public string Label { get; }
    public int Frames { get; }
    public int SampleRate { get; }
    public double PeriodMs => Frames * 1000.0 / SampleRate;
    public double DeadlineMs { get; }

    public readonly Histogram RoundTrip = new();  // от входа до забранного выхода — то, что платит аудиопоток
    public readonly Histogram Overhead = new();   // RoundTrip минус process: стоимость обмена
    public readonly Histogram HostWake = new();   // SetEvent(Request) → хост проснулся
    public readonly Histogram Return = new();     // хост перед SetEvent(Done) → микшер увидел ответ
    public readonly Histogram ProcessTime = new();
    public readonly Histogram TimerLate = new();  // опоздание пробуждения по таймеру (для контекста)

    public long Blocks, Processed, Missed, Busy, Mismatches, BadResults, SpinHits, Overruns;
    /// <summary>Номера первых просроченных блоков — понять, случайны ли они или это старт.</summary>
    public readonly long[] MissedAt = new long[32];
    /// <summary>Сколько аудиопоток провёл в обмене (стена): верхняя оценка его CPU на обмен.</summary>
    public long ExchangeWallTicks;
    public readonly long[] OverGatePerMinute;
    public readonly double[] MaxOverheadPerMinute;
    public double ThreadCpuSeconds { get; private set; }
    public TimeSpan Elapsed { get; private set; }
    public Exception? Error { get; private set; }

    public BenchRun(string label, HostChannel channel, int frames, int sampleRate, double deadlinePercent,
        TimeSpan duration, double gateUs, CancellationToken cancel)
    {
        Label = label;
        _channel = channel;
        Frames = frames;
        SampleRate = sampleRate;
        _duration = duration;
        _cancel = cancel;
        _gateUs = gateUs;
        _periodTicks = (double)frames * Stopwatch.Frequency / sampleRate;
        _deadlineTicks = (long)(_periodTicks * deadlinePercent / 100);
        DeadlineMs = PeriodMs * deadlinePercent / 100;
        int minutes = (int)Math.Ceiling(duration.TotalMinutes) + 2;
        OverGatePerMinute = new long[minutes];
        MaxOverheadPerMinute = new double[minutes];
    }

    public void Start()
    {
        _thread = new Thread(Loop) { Name = $"bench {Label}", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Join() => _thread?.Join();

    private volatile bool _finished;
    public bool Finished => _finished;

    private void Loop()
    {
        try { Run(); }
        catch (Exception e) { Error = e; }
        finally { _finished = true; }
    }

    private void Run()
    {
        uint taskIndex = 0;
        IntPtr mmcss = Native.AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        IntPtr timer = Native.CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero,
            Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
        if (timer == IntPtr.Zero) throw Native.LastError("CreateWaitableTimerEx");

        // сигнал: 440 Гц слева, 660 Гц справа, −6 dBFS; таблица на секунду плюс запас под блок без переноса
        int tableLen = SampleRate + Frames;
        var sigL = new float[tableLen];
        var sigR = new float[tableLen];
        for (int i = 0; i < tableLen; i++)
        {
            sigL[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / SampleRate));
            sigR[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 660 * i / SampleRate));
        }
        var outL = new float[Frames];
        var outR = new float[Frames];
        var t = new BlockTiming();
        double usPerTick = 1e6 / Stopwatch.Frequency;
        long minuteTicks = 60 * Stopwatch.Frequency;

        Native.GetThreadTimes(Native.GetCurrentThread(), out _, out _, out long k0, out long u0);
        long start = Stopwatch.GetTimestamp();
        long end = start + (long)(_duration.TotalSeconds * Stopwatch.Frequency);
        double next = start + _periodTicks;
        int pos = 0;

        while (!_cancel.IsCancellationRequested)
        {
            long target = (long)next;
            long now = Stopwatch.GetTimestamp();
            if (now >= end) break;
            long wait = target - now;
            if (wait > 0)
            {
                long due = -(wait * 10_000_000 / Stopwatch.Frequency);
                if (due < 0)
                {
                    Native.SetWaitableTimer(timer, &due, 0, IntPtr.Zero, IntPtr.Zero, 0);
                    Native.WaitForSingleObject(timer, Native.INFINITE);
                }
            }
            long woke = Stopwatch.GetTimestamp();
            TimerLate.RecordTicks(woke - target);

            var inL = new ReadOnlySpan<float>(sigL, pos, Frames);
            var inR = new ReadOnlySpan<float>(sigR, pos, Frames);
            var outcome = _channel.Process(inL, inR, outL, outR, woke + _deadlineTicks, ref t);
            Blocks++;
            if (outcome != BlockOutcome.Busy) ExchangeWallTicks += (outcome == BlockOutcome.Processed ? t.End : t.Seen) - t.Start;
            switch (outcome)
            {
                case BlockOutcome.Processed:
                    Processed++;
                    if (t.ViaSpin) SpinHits++;
                    if (t.Result != 0) BadResults++;
                    long roundTrip = t.End - t.Start;
                    long overhead = roundTrip - t.ProcessTicks;
                    RoundTrip.RecordTicks(roundTrip);
                    Overhead.RecordTicks(overhead);
                    HostWake.RecordTicks(t.HostWake - t.Request);
                    Return.RecordTicks(t.Seen - t.HostDone);
                    ProcessTime.RecordTicks(t.ProcessTicks);
                    int minute = (int)((woke - start) / minuteTicks);
                    if (minute < OverGatePerMinute.Length)
                    {
                        double us = overhead * usPerTick;
                        if (us > _gateUs) OverGatePerMinute[minute]++;
                        if (us > MaxOverheadPerMinute[minute]) MaxOverheadPerMinute[minute] = us;
                    }
                    // AGain с усилением 1 и копирование — побитово тот же сигнал
                    if (!inL.SequenceEqual(outL) || !inR.SequenceEqual(outR)) Mismatches++;
                    break;
                case BlockOutcome.Missed:
                    if (Missed < MissedAt.Length) MissedAt[Missed] = Blocks - 1;
                    Missed++;
                    break;
                case BlockOutcome.Busy:
                    Busy++;
                    break;
            }

            pos += Frames;
            if (pos >= SampleRate) pos -= SampleRate;
            next += _periodTicks;
            long after = Stopwatch.GetTimestamp();
            if (after > next + _periodTicks)
            {
                // отстали больше чем на период (поток не дали вовремя): не догонять пачкой
                Overruns++;
                next = after + _periodTicks;
            }
        }

        Elapsed = Stopwatch.GetElapsedTime(start);
        Native.GetThreadTimes(Native.GetCurrentThread(), out _, out _, out long k1, out long u1);
        ThreadCpuSeconds = (k1 - k0 + u1 - u0) / 1e7;
        Native.CloseHandle(timer);
        if (mmcss != IntPtr.Zero) Native.AvRevertMmThreadCharacteristics(mmcss);
    }
}
