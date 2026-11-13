using System.Diagnostics;
using System.Text;

namespace Mixer.Plugins.Host;

public enum BlockOutcome
{
    /// <summary>Хост ответил до дедлайна, выход забран.</summary>
    Processed,
    /// <summary>Дедлайн прошёл: блок идёт сухим, поздний ответ будет выброшен по номеру.</summary>
    Missed,
    /// <summary>Хост ещё считает прошлый (просроченный) блок: новый не отправлен, блок сухой.</summary>
    Busy,
    /// <summary>Хост ответил вовремя, но не считал: плагин приостановлен (смена частоты, задержки, состояния). Блок сухой.</summary>
    Bypassed,
}

/// <summary>Тайминги последнего обработанного блока, тики QPC (<see cref="Stopwatch.GetTimestamp"/>).</summary>
public struct BlockTiming
{
    public long Start;       // перед копированием входа
    public long Request;     // перед SetEvent(Request)
    public long HostWake;    // хост проснулся
    public long HostDone;    // хост перед SetEvent(Done)
    public long Seen;        // микшер увидел ответ
    public long End;         // выход скопирован
    public long ProcessTicks;
    public bool ViaSpin;     // ответ пойман активным ожиданием, без перехода в ядро
    public int Result;       // tresult от process (0 — успех)
}

/// <summary>
/// Сторона микшера в аудиоканале одного хоста: секция общей памяти и события Request/Done.
/// <see cref="Process"/> вызывается из одного потока за раз (аудиопоток узла) и ничего не выделяет.
/// Шаг: скопировать вход, взвести Request, ждать Done активно до 50 мкс, потом в ядре — вместе с таймером дедлайна.
/// </summary>
public sealed unsafe class AudioLink : IDisposable
{
    private readonly IntPtr _mapping;
    private readonly byte* _base;
    private readonly SharedHeader* _h;
    private readonly IntPtr _request;
    private readonly IntPtr _done;
    private readonly IntPtr _deadlineTimer;
    private readonly float* _in0, _in1, _out0, _out1;
    private readonly long _spinTicks;
    private ulong _lastSent;
    private bool _disposed;

    /// <summary>Префикс имён объектов (для командной строки хоста).</summary>
    public string Name { get; }
    public int MaxFrames { get; }

    /// <summary>Задержка, которую хост сообщил последней (вместе с «ровными блоками»).</summary>
    public int LatencySamples => Volatile.Read(ref _h->LatencySamples);
    public int PluginInChannels => _h->PluginInChannels;
    public int PluginOutChannels => _h->PluginOutChannels;
    internal HostState State => (HostState)Volatile.Read(ref _h->State);
    /// <summary>Время обработки последнего блока в хосте, тики QPC.</summary>
    public long LastProcessTicks => Volatile.Read(ref _h->ProcessTicks);

    internal AudioLink(string name, int sampleRate, int maxFrames, double spinMicroseconds = 50)
    {
        Name = name;
        MaxFrames = maxFrames;
        _spinTicks = (long)(spinMicroseconds * Stopwatch.Frequency / 1e6);

        long bytes = SharedLayout.SectionBytes(maxFrames);
        _mapping = Native.CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, Native.PAGE_READWRITE,
            (uint)(bytes >> 32), (uint)bytes, name + ".shm");
        if (_mapping == IntPtr.Zero) throw Native.LastError("CreateFileMapping");
        _base = (byte*)Native.MapViewOfFile(_mapping, Native.FILE_MAP_ALL_ACCESS, 0, 0, (nuint)bytes);
        if (_base == null) throw Native.LastError("MapViewOfFile");
        _h = (SharedHeader*)_base;
        // все страницы секции сразу: иначе первые блоки платят за страничные промахи в аудиопотоке
        new Span<byte>(_base, (int)bytes).Clear();

        _h->Magic = SharedLayout.Magic;
        _h->Version = SharedLayout.ProtocolVersion;
        _h->SampleRate = (uint)sampleRate;
        _h->MaxFrames = (uint)maxFrames;
        _h->Channels = SharedLayout.Channels;

        _in0 = (float*)(_base + SharedLayout.InputOffset(maxFrames, 0));
        _in1 = (float*)(_base + SharedLayout.InputOffset(maxFrames, 1));
        _out0 = (float*)(_base + SharedLayout.OutputOffset(maxFrames, 0));
        _out1 = (float*)(_base + SharedLayout.OutputOffset(maxFrames, 1));

        _request = Native.CreateEventW(IntPtr.Zero, 0, 0, name + ".req");
        _done = Native.CreateEventW(IntPtr.Zero, 0, 0, name + ".done");
        if (_request == IntPtr.Zero || _done == IntPtr.Zero) throw Native.LastError("CreateEvent");
        _deadlineTimer = Native.CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero,
            Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
        if (_deadlineTimer == IntPtr.Zero) throw Native.LastError("CreateWaitableTimerEx");
    }

    internal string ReadError()
    {
        var span = new ReadOnlySpan<byte>(_base + SharedLayout.ErrorOffset, SharedLayout.ErrorBytes);
        int end = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }

    /// <summary>
    /// Несколько блоков тишины с секундным дедлайном до начала работы: первый process у плагина часто
    /// медленный (ленивая инициализация, промахи кэша). Слот в это время ещё «загружается».
    /// Приостановленный хост не провал: плагин сразу после загрузки позвал restartComponent (CHOWTapeModel), хост
    /// его перенастраивает — ждём, пока закончит, до 10 с.
    /// </summary>
    internal void WarmUp(int blocks = 8)
    {
        int frames = Math.Min(MaxFrames, 480);
        var silence = new float[frames];
        var sink = new float[frames];
        long giveUp = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
        for (int done = 0; done < blocks;)
        {
            var outcome = Process(silence, silence, sink, sink, Stopwatch.GetTimestamp() + Stopwatch.Frequency);
            if (outcome == BlockOutcome.Processed) done++;
            else if (outcome != BlockOutcome.Bypassed)
                throw new TimeoutException($"host did not answer a warm-up block within 1 s ({outcome})");
            else if (Stopwatch.GetTimestamp() > giveUp)
                throw new TimeoutException("host stayed suspended for 10 s after load");
            else Thread.Sleep(5);
        }
    }

    public BlockOutcome Process(ReadOnlySpan<float> inL, ReadOnlySpan<float> inR, Span<float> outL, Span<float> outR,
        long deadlineQpc)
    {
        var t = new BlockTiming();
        return Process(inL, inR, outL, outR, deadlineQpc, ref t);
    }

    /// <summary>
    /// Один блок: отдать вход хосту и дождаться ответа до <paramref name="deadlineQpc"/>.
    /// При любом исходе, кроме <see cref="BlockOutcome.Processed"/>, out не трогается: решает вызывающий (сухой сигнал).
    /// </summary>
    public BlockOutcome Process(ReadOnlySpan<float> inL, ReadOnlySpan<float> inR, Span<float> outL, Span<float> outR,
        long deadlineQpc, ref BlockTiming t)
    {
        int frames = inL.Length;
        if (frames > MaxFrames) throw new ArgumentOutOfRangeException(nameof(inL), "block is larger than MaxFrames");
        t.Start = Stopwatch.GetTimestamp();

        // прошлый блок просрочен и хост его ещё считает: вход трогать нельзя, он его читает
        if (Volatile.Read(ref _h->DoneSeq) != _lastSent) return BlockOutcome.Busy;

        inL.CopyTo(new Span<float>(_in0, frames));
        inR.CopyTo(new Span<float>(_in1, frames));
        ulong seq = ++_lastSent;
        _h->Frames = (uint)frames;
        t.Request = Stopwatch.GetTimestamp();
        _h->RequestQpc = t.Request;
        Volatile.Write(ref _h->RequestSeq, seq);
        Native.SetEvent(_request);

        bool done = false;
        long spinUntil = Math.Min(t.Request + _spinTicks, deadlineQpc);
        while (true)
        {
            if (Volatile.Read(ref _h->DoneSeq) == seq) { done = true; t.ViaSpin = true; break; }
            if (Stopwatch.GetTimestamp() >= spinUntil) break;
            Thread.SpinWait(8);
        }

        if (!done)
        {
            t.ViaSpin = false;
            IntPtr* handles = stackalloc IntPtr[2] { _done, _deadlineTimer };
            while (true)
            {
                if (Volatile.Read(ref _h->DoneSeq) == seq) { done = true; break; }
                long now = Stopwatch.GetTimestamp();
                long remaining = deadlineQpc - now;
                if (remaining <= 0) break;
                // относительный срок в 100 нс; округление вверх, чтобы не проснуться раньше дедлайна
                long due = -Math.Max(1, (remaining * 10_000_000 + Stopwatch.Frequency - 1) / Stopwatch.Frequency);
                // Done от просроченного блока тоже будит: проверка номера в начале цикла это отсеет.
                // Если таймер не взвёлся, остаётся грубый таймаут ожидания — аудиопоток не повиснет.
                uint backstopMs = (uint)(remaining * 1000 / Stopwatch.Frequency) + 2;
                if (Native.SetWaitableTimer(_deadlineTimer, &due, 0, IntPtr.Zero, IntPtr.Zero, 0) != 0)
                    Native.WaitForMultipleObjects(2, handles, 0, backstopMs);
                else
                    Native.WaitForSingleObject(_done, backstopMs);
            }
        }

        t.Seen = Stopwatch.GetTimestamp();
        if (!done) return BlockOutcome.Missed;

        t.HostWake = _h->WakeQpc;
        t.HostDone = _h->DoneQpc;
        t.ProcessTicks = _h->ProcessTicks;
        t.Result = _h->ProcessResult;
        if ((_h->DoneFlags & SharedLayout.DoneBypassed) != 0)
        {
            t.End = t.Seen;
            return BlockOutcome.Bypassed;
        }
        new ReadOnlySpan<float>(_out0, frames).CopyTo(outL);
        new ReadOnlySpan<float>(_out1, frames).CopyTo(outR);
        t.End = Stopwatch.GetTimestamp();
        return BlockOutcome.Processed;
    }

    /// <summary>Попросить аудиопоток хоста выйти (вместе с quit по каналу управления).</summary>
    internal void SignalStop()
    {
        if (_disposed) return;
        _h->Flags |= SharedLayout.FlagStop;
        Native.SetEvent(_request);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Native.CloseHandle(_deadlineTimer);
        Native.CloseHandle(_request);
        Native.CloseHandle(_done);
        Native.UnmapViewOfFile((IntPtr)_base);
        Native.CloseHandle(_mapping);
    }
}
