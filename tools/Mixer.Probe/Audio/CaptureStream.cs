using System.Diagnostics;
using System.Runtime.InteropServices;
using Mixer.Probe.Interop;

namespace Mixer.Probe.Audio;

/// <summary>Пакет захвата: позиция устройства в кадрах и время первого кадра по QPC в единицах 100 нс.</summary>
public readonly record struct PacketInfo(int Frames, uint Flags, ulong DevicePosition, ulong Qpc100ns)
{
    public bool Silent => (Flags & AudioConst.AUDCLNT_BUFFERFLAGS_SILENT) != 0;
    public bool Discontinuity => (Flags & AudioConst.AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0;
}

/// <summary>Вызывается из потока захвата на каждый пакет; samples — float interleaved в формате потока (тишина — нули).</summary>
public delegate void PacketHandler(in PacketInfo packet, ReadOnlySpan<float> samples);

/// <summary>
/// Событийный захват из уже активированного IAudioClient (process loopback или loopback устройства) в WAV и LevelStats.
/// Это утилита проверки: запись в файл идёт прямо из потока захвата, в движке так делать нельзя.
/// </summary>
public sealed class CaptureStream : IDisposable
{
    private const long BufferDuration100ns = 10_000_000; // 1 с: утилите не важна задержка, важна устойчивость

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly EventWaitHandle _event;
    private readonly Stopwatch _clock;
    private readonly string? _wavPath;
    private WavWriter? _wav;
    private Thread? _thread;
    private volatile bool _stop;
    private bool _started;
    private byte[] _bytes = Array.Empty<byte>();
    private float[] _floats = Array.Empty<float>();

    public string Name { get; }
    public AudioFormat Format { get; }
    public LevelStats Stats { get; }
    public int ErrorHr { get; private set; }

    /// <summary>Stopwatch-время IAudioClient.Start и первого пакета (0 — пакетов ещё не было).</summary>
    public long StartedTicks { get; private set; }
    public long FirstPacketTicks { get; private set; }

    /// <summary>От Start до первого пакета, мс; NaN — пакетов не было.</summary>
    public double FirstPacketMs => FirstPacketTicks == 0 ? double.NaN : (FirstPacketTicks - StartedTicks) * 1000.0 / Stopwatch.Frequency;
    public string? WavPath => _wavPath;

    /// <summary>Трассировка вызовов COM (для поиска зависаний). null — без трассировки.</summary>
    public StepLog? Trace { get; set; }
    public bool UseMmcss { get; set; } = true;

    /// <summary>Обработчик каждого пакета (уровень, кольцо, отметки времени). null — только статистика и WAV.</summary>
    public PacketHandler? OnPacket { get; set; }
    public ThreadPriority Priority { get; set; } = ThreadPriority.Highest;

    private void Step(string what, Action action)
    {
        if (Trace is null) action();
        else Trace.Run($"{Name}: {what}", action);
    }

    private CaptureStream(string name, IAudioClient client, IAudioCaptureClient capture, EventWaitHandle evt,
        AudioFormat format, string? wavPath, Stopwatch clock, double maxSeconds)
    {
        Name = name;
        _client = client;
        _capture = capture;
        _event = evt;
        Format = format;
        _wavPath = wavPath;
        _clock = clock;
        Stats = new LevelStats(maxSeconds, format.Channels);
    }

    /// <summary>
    /// Инициализирует клиента. requested = null берёт GetMixFormat (для loopback устройства).
    /// При ошибке возвращает (null, hr) и освобождает клиента.
    /// </summary>
    public static (CaptureStream? Stream, int Hr) TryCreate(string name, IAudioClient client, AudioFormat? requested,
        uint streamFlags, string? wavPath, Stopwatch clock, double maxSeconds = 3600)
    {
        int hr;
        AudioFormat format;
        if (requested is null)
        {
            hr = client.GetMixFormat(out IntPtr pMix);
            if (hr < 0) return Fail(client, hr);
            format = AudioFormat.FromPointer(pMix);
            Marshal.FreeCoTaskMem(pMix);
        }
        else
        {
            format = requested;
        }

        return TryCreateWith(name, client, format,
            p => client.Initialize(AudioConst.AUDCLNT_SHAREMODE_SHARED, streamFlags, BufferDuration100ns, 0, p, IntPtr.Zero),
            wavPath, clock, maxSeconds);
    }

    /// <summary>
    /// То же со своей инициализацией: initialize получает указатель на формат и возвращает HRESULT
    /// (например, IAudioClient3.InitializeSharedAudioStream). Поток должен быть событийным.
    /// </summary>
    public static (CaptureStream? Stream, int Hr) TryCreateWith(string name, IAudioClient client, AudioFormat format,
        Func<IntPtr, int> initialize, string? wavPath, Stopwatch clock, double maxSeconds = 3600)
    {
        int hr;
        IntPtr pFormat = format.AllocCoTaskMem();
        try
        {
            hr = initialize(pFormat);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pFormat);
        }
        if (hr < 0) return Fail(client, hr);

        Guid iid = typeof(IAudioCaptureClient).GUID;
        hr = client.GetService(ref iid, out object svc);
        if (hr < 0) return Fail(client, hr);

        var evt = new EventWaitHandle(false, EventResetMode.AutoReset);
        hr = client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle());
        if (hr < 0)
        {
            evt.Dispose();
            Marshal.ReleaseComObject(svc);
            return Fail(client, hr);
        }

        return (new CaptureStream(name, client, (IAudioCaptureClient)svc, evt, format, wavPath, clock, maxSeconds), 0);
    }

    private static (CaptureStream?, int) Fail(IAudioClient client, int hr)
    {
        Marshal.ReleaseComObject(client);
        return (null, hr);
    }

    public void Start()
    {
        if (_wavPath is not null) _wav = new WavWriter(_wavPath, Format);
        StartedTicks = Stopwatch.GetTimestamp();
        Step("IAudioClient.Start", () => AudioException.Check(_client.Start(), $"{Name}: IAudioClient.Start"));
        _started = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "capture " + Name, Priority = Priority };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Loop()
    {
        uint taskIndex = 0;
        IntPtr mmcss = IntPtr.Zero;
        if (UseMmcss) Step("AvSetMmThreadCharacteristics(Pro Audio)", () => mmcss = NativeMethods.AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex));
        bool first = true;
        try
        {
            while (!_stop)
            {
                _event.WaitOne(50);
                while (true)
                {
                    int hr = _capture.GetNextPacketSize(out uint packet);
                    if (hr < 0) { ErrorHr = hr; return; }
                    if (packet == 0) break;

                    hr = _capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpc);
                    if (hr < 0) { ErrorHr = hr; return; }
                    if (frames > 0) Consume(data, (int)frames, flags, devicePosition, qpc);
                    _capture.ReleaseBuffer(frames);
                    if (first)
                    {
                        FirstPacketTicks = Stopwatch.GetTimestamp();
                        Trace?.Log($"{Name}: первый пакет, {frames} кадров");
                        first = false;
                    }
                }
            }
        }
        finally
        {
            if (mmcss != IntPtr.Zero) Step("AvRevertMmThreadCharacteristics", () => NativeMethods.AvRevertMmThreadCharacteristics(mmcss));
            Trace?.Log($"{Name}: поток захвата вышел{(ErrorHr != 0 ? ", ошибка " + Hr.Describe(ErrorHr) : "")}");
        }
    }

    private void Consume(IntPtr data, int frames, uint flags, ulong devicePosition, ulong qpc)
    {
        int bytes = frames * Format.BlockAlign;
        int samples = frames * Format.Channels;
        if (_bytes.Length < bytes) _bytes = new byte[bytes * 2];
        if (_floats.Length < samples) _floats = new float[samples * 2];

        bool silent = (flags & AudioConst.AUDCLNT_BUFFERFLAGS_SILENT) != 0;
        if (silent) Array.Clear(_bytes, 0, bytes);
        else Marshal.Copy(data, _bytes, 0, bytes);

        _wav?.Write(_bytes, bytes);
        Format.Decode(_bytes.AsSpan(0, bytes), _floats.AsSpan(0, samples));
        Stats.Add(_clock.Elapsed.TotalSeconds, frames, _floats.AsSpan(0, samples), silent,
            (flags & AudioConst.AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0);
        OnPacket?.Invoke(new PacketInfo(frames, flags, devicePosition, qpc), _floats.AsSpan(0, samples));
    }

    /// <summary>Просит поток захвата выйти и ждёт его. Клиент при этом ещё не остановлен.</summary>
    public void StopThread()
    {
        if (_thread is null) return;
        _stop = true;
        var t = _thread;
        Step("Join потока захвата", () =>
        {
            if (!t.Join(2000)) Trace?.Log($"{Name}: поток захвата не вышел за 2 с");
        });
        _thread = null;
    }

    /// <summary>IAudioClient.Stop и закрытие WAV.</summary>
    public void StopClient()
    {
        if (!_started) return;
        _started = false;
        Step("IAudioClient.Stop", () => _client.Stop());
        _wav?.Dispose();
        _wav = null;
    }

    public void Stop()
    {
        StopThread();
        StopClient();
    }

    public void Dispose()
    {
        Stop();
        Step("Release IAudioCaptureClient", () => Marshal.ReleaseComObject(_capture));
        Step("Release IAudioClient", () => Marshal.ReleaseComObject(_client));
        _event.Dispose();
    }
}
