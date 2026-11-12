using System.Diagnostics;
using System.Text;

namespace Mixer.PluginBench;

internal enum BlockOutcome
{
    /// <summary>Хост ответил до дедлайна, выход забран.</summary>
    Processed,
    /// <summary>Дедлайн прошёл: блок идёт сухим, поздний ответ будет выброшен по номеру.</summary>
    Missed,
    /// <summary>Хост ещё считает прошлый (просроченный) блок: новый не отправлен, блок сухой.</summary>
    Busy,
}

/// <summary>Тайминги последнего обработанного блока, тики QPC.</summary>
internal struct BlockTiming
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

public sealed record HostStartInfo(
    string HostExe,
    string? PluginPath,
    int SampleRate,
    int MaxFrames,
    int StallEvery = 0,
    int StallMs = 0,
    TimeSpan? ReadyTimeout = null);

/// <summary>
/// Сторона микшера в аудиоканале: секция общей памяти, события Request/Done и процесс хоста.
/// <see cref="Process"/> вызывается из аудиопотока и ничего не выделяет. Прототип транспорта будущего PluginSlot.
/// </summary>
internal sealed unsafe class HostChannel : IDisposable
{
    private static int s_counter;
    private static readonly Lazy<IntPtr> s_job = new(CreateKillOnCloseJob);

    private readonly IntPtr _mapping;
    private readonly byte* _base;
    private readonly SharedHeader* _h;
    private readonly IntPtr _request;
    private readonly IntPtr _done;
    private readonly IntPtr _deadlineTimer;
    private readonly float* _in0, _in1, _out0, _out1;
    private readonly long _spinTicks;
    private readonly StringBuilder _stderr = new();
    private ulong _lastSent;

    public Process HostProcess { get; }
    public int MaxFrames { get; }
    public int LatencySamples => _h->LatencySamples;
    public int PluginInChannels => _h->PluginInChannels;
    public int PluginOutChannels => _h->PluginOutChannels;
    /// <summary>От запуска процесса до состояния Ready.</summary>
    public TimeSpan StartupTime { get; }

    private HostChannel(HostStartInfo info, double spinMicroseconds)
    {
        MaxFrames = info.MaxFrames;
        _spinTicks = (long)(spinMicroseconds * Stopwatch.Frequency / 1e6);
        string prefix = $@"Local\Mixer.PH.{Environment.ProcessId}.{Interlocked.Increment(ref s_counter)}";

        long bytes = SharedLayout.SectionBytes(info.MaxFrames);
        _mapping = Native.CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, Native.PAGE_READWRITE,
            (uint)(bytes >> 32), (uint)bytes, prefix + ".shm");
        if (_mapping == IntPtr.Zero) throw Native.LastError("CreateFileMapping");
        _base = (byte*)Native.MapViewOfFile(_mapping, Native.FILE_MAP_ALL_ACCESS, 0, 0, (nuint)bytes);
        if (_base == null) throw Native.LastError("MapViewOfFile");
        _h = (SharedHeader*)_base;
        // все страницы секции сразу: иначе первые блоки платят за страничные промахи в аудиопотоке
        new Span<byte>(_base, (int)bytes).Clear();

        _h->Magic = SharedLayout.Magic;
        _h->Version = SharedLayout.ProtocolVersion;
        _h->SampleRate = (uint)info.SampleRate;
        _h->MaxFrames = (uint)info.MaxFrames;
        _h->Channels = SharedLayout.Channels;

        _in0 = (float*)(_base + SharedLayout.InputOffset(MaxFrames, 0));
        _in1 = (float*)(_base + SharedLayout.InputOffset(MaxFrames, 1));
        _out0 = (float*)(_base + SharedLayout.OutputOffset(MaxFrames, 0));
        _out1 = (float*)(_base + SharedLayout.OutputOffset(MaxFrames, 1));

        _request = Native.CreateEventW(IntPtr.Zero, 0, 0, prefix + ".req");
        _done = Native.CreateEventW(IntPtr.Zero, 0, 0, prefix + ".done");
        if (_request == IntPtr.Zero || _done == IntPtr.Zero) throw Native.LastError("CreateEvent");
        _deadlineTimer = Native.CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero,
            Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
        if (_deadlineTimer == IntPtr.Zero) throw Native.LastError("CreateWaitableTimerEx");

        var psi = new ProcessStartInfo(info.HostExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("--channel");
        psi.ArgumentList.Add(prefix);
        psi.ArgumentList.Add("--parent");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        if (info.PluginPath is null)
        {
            psi.ArgumentList.Add("--builtin");
            psi.ArgumentList.Add("copy");
        }
        else
        {
            psi.ArgumentList.Add("--plugin");
            psi.ArgumentList.Add(info.PluginPath);
        }
        if (info.StallEvery > 0)
        {
            psi.ArgumentList.Add("--stall-every");
            psi.ArgumentList.Add(info.StallEvery.ToString());
            psi.ArgumentList.Add("--stall-ms");
            psi.ArgumentList.Add(info.StallMs.ToString());
        }

        var started = Stopwatch.StartNew();
        HostProcess = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("host did not start");
        if (Native.AssignProcessToJobObject(s_job.Value, HostProcess.Handle) == 0)
            throw Native.LastError("AssignProcessToJobObject");
        HostProcess.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_stderr) _stderr.AppendLine(e.Data); };
        HostProcess.OutputDataReceived += (_, e) => { if (e.Data != null) lock (_stderr) _stderr.AppendLine(e.Data); };
        HostProcess.BeginErrorReadLine();
        HostProcess.BeginOutputReadLine();

        var timeout = info.ReadyTimeout ?? TimeSpan.FromSeconds(10);
        while (true)
        {
            var state = (HostState)Volatile.Read(ref _h->State);
            if (state == HostState.Ready) break;
            if (state == HostState.Failed)
                throw new InvalidOperationException($"host failed to load: {ReadError()}");
            if (HostProcess.HasExited)
            {
                HostProcess.WaitForExit();
                throw new InvalidOperationException($"host exited with code {HostProcess.ExitCode}: {Stderr()}");
            }
            if (started.Elapsed > timeout)
                throw new TimeoutException($"host not ready after {timeout.TotalSeconds:0} s");
            Thread.Sleep(2);
        }
        StartupTime = started.Elapsed;
        WarmUp();
    }

    /// <summary>
    /// Несколько блоков тишины с секундным дедлайном до начала работы: первый process у плагина часто
    /// медленный (ленивая инициализация, промахи кэша). Слот в это время ещё «загружается».
    /// </summary>
    private void WarmUp()
    {
        var silence = new float[MaxFrames];
        var sink = new float[MaxFrames];
        var t = new BlockTiming();
        for (int i = 0; i < 8; i++)
        {
            var outcome = Process(silence, silence, sink, sink, Stopwatch.GetTimestamp() + Stopwatch.Frequency, ref t);
            if (outcome != BlockOutcome.Processed)
                throw new TimeoutException("host did not answer a warm-up block within 1 s");
        }
    }

    public static HostChannel Start(HostStartInfo info, double spinMicroseconds = 50) => new(info, spinMicroseconds);

    public string Stderr()
    {
        lock (_stderr) return _stderr.ToString().Trim();
    }

    private string ReadError()
    {
        var span = new ReadOnlySpan<byte>(_base + SharedLayout.ErrorOffset, SharedLayout.ErrorBytes);
        int end = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }

    /// <summary>
    /// Один блок: отдать вход хосту и дождаться ответа до <paramref name="deadlineQpc"/>.
    /// Ожидание: активное до spin-порога, затем ядро (Done или таймер дедлайна). Без аллокаций.
    /// При любом исходе, кроме Processed, содержимое out не трогается: решает вызывающий (сухой сигнал).
    /// </summary>
    public BlockOutcome Process(ReadOnlySpan<float> inL, ReadOnlySpan<float> inR, Span<float> outL, Span<float> outR,
        long deadlineQpc, ref BlockTiming t)
    {
        int frames = inL.Length;
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

        new ReadOnlySpan<float>(_out0, frames).CopyTo(outL);
        new ReadOnlySpan<float>(_out1, frames).CopyTo(outR);
        t.HostWake = _h->WakeQpc;
        t.HostDone = _h->DoneQpc;
        t.ProcessTicks = _h->ProcessTicks;
        t.Result = _h->ProcessResult;
        t.End = Stopwatch.GetTimestamp();
        return BlockOutcome.Processed;
    }

    public void Dispose()
    {
        try
        {
            if (!HostProcess.HasExited)
            {
                _h->Flags |= SharedLayout.FlagStop;
                Native.SetEvent(_request);
                if (!HostProcess.WaitForExit(3000)) HostProcess.Kill();
            }
        }
        catch (InvalidOperationException) { }
        HostProcess.Dispose();
        Native.CloseHandle(_deadlineTimer);
        Native.CloseHandle(_request);
        Native.CloseHandle(_done);
        Native.UnmapViewOfFile((IntPtr)_base);
        Native.CloseHandle(_mapping);
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        IntPtr job = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw Native.LastError("CreateJobObject");
        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref info,
                (uint)sizeof(Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION)) == 0)
            throw Native.LastError("SetInformationJobObject");
        return job; // не закрываем: закроется с процессом бенчмарка и заберёт хосты
    }
}
