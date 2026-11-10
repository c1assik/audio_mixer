using System.Diagnostics;
using System.Runtime.InteropServices;
using Mixer.Probe.Interop;

namespace Mixer.Probe.Audio;

/// <summary>Заполняет stereo (interleaved L/R, frames × 2) звуком для очередного буфера. Тишина — оставить нули.</summary>
public delegate void RenderSource(Span<float> stereo);

/// <summary>
/// Событийный поток воспроизведения общего режима. Источник даёт стерео, поток раскладывает его в FL/FR формата устройства.
/// Считает пробуждения, интервалы между ними и опустошения буфера (на пробуждении padding = 0).
/// </summary>
public sealed class RenderStream : IDisposable
{
    private readonly IAudioClient _client;
    private readonly IAudioRenderClient _render;
    private readonly EventWaitHandle _event;
    private readonly uint _bufferFrames;
    private readonly float[] _intervalsMs;
    private float[] _stereo = Array.Empty<float>();
    private Thread? _thread;
    private volatile bool _stop;
    private bool _started;
    private int _intervalCount;

    public string Name { get; }
    public AudioFormat Format { get; }
    public uint BufferFrames => _bufferFrames;

    /// <summary>Сколько кадров держать в буфере устройства; 0 — весь буфер.</summary>
    public int QueueFrames { get; set; }

    public RenderSource? Source { get; set; }

    public int Wakes { get; private set; }
    public int Underruns { get; private set; }
    public int Timeouts { get; private set; }
    public long FramesWritten { get; private set; }
    public int ErrorHr { get; private set; }

    /// <summary>Время (Stopwatch) каждого опустошения: по ним считается частота сбоев за минуту.</summary>
    public List<long> UnderrunTimes { get; } = new(1024);

    private RenderStream(string name, IAudioClient client, IAudioRenderClient render, EventWaitHandle evt, AudioFormat format,
        uint bufferFrames, double maxSeconds)
    {
        Name = name;
        _client = client;
        _render = render;
        _event = evt;
        Format = format;
        _bufferFrames = bufferFrames;
        _intervalsMs = new float[(int)Math.Min(4_000_000, maxSeconds * 1000 + 1000)]; // до пробуждения на каждую мс
    }

    /// <summary>Обычный общий режим: Initialize с буфером bufferHns и периодом устройства по умолчанию.</summary>
    public static (RenderStream? Stream, int Hr) TryCreate(string name, IAudioClient client, AudioFormat format, long bufferHns, double maxSeconds) =>
        TryCreateWith(name, client, format,
            p => client.Initialize(AudioConst.AUDCLNT_SHAREMODE_SHARED, AudioConst.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, bufferHns, 0, p, IntPtr.Zero),
            maxSeconds);

    /// <summary>Своя инициализация (например, IAudioClient3.InitializeSharedAudioStream). Поток должен быть событийным.</summary>
    public static (RenderStream? Stream, int Hr) TryCreateWith(string name, IAudioClient client, AudioFormat format,
        Func<IntPtr, int> initialize, double maxSeconds)
    {
        int bytesPerSample = format.BlockAlign / format.Channels;
        if (!(format.IsFloat && bytesPerSample == 4) && !(!format.IsFloat && bytesPerSample is 2 or 4))
            throw new AudioException($"{name}: формат {format} не поддержан");

        IntPtr pFormat = format.AllocCoTaskMem();
        int hr;
        try
        {
            hr = initialize(pFormat);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pFormat);
        }
        if (hr < 0) return Fail(client, hr);

        hr = client.GetBufferSize(out uint bufferFrames);
        if (hr < 0) return Fail(client, hr);
        Guid iid = typeof(IAudioRenderClient).GUID;
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
        return (new RenderStream(name, client, (IAudioRenderClient)svc, evt, format, bufferFrames, maxSeconds), 0);
    }

    private static (RenderStream?, int) Fail(IAudioClient client, int hr)
    {
        Marshal.ReleaseComObject(client);
        return (null, hr);
    }

    public void Start()
    {
        _stereo = new float[_bufferFrames * 2];
        int hr = Fill(); // предзаполнение, чтобы старт не начинался с опустошения
        AudioException.Check(hr, $"{Name}: первый буфер");
        AudioException.Check(_client.Start(), $"{Name}: IAudioClient.Start");
        _started = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "render " + Name, Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Loop()
    {
        uint taskIndex = 0;
        IntPtr mmcss = NativeMethods.AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        long last = 0;
        try
        {
            while (!_stop)
            {
                if (!_event.WaitOne(200))
                {
                    Timeouts++;
                    continue;
                }
                long now = Stopwatch.GetTimestamp();
                if (last != 0 && _intervalCount < _intervalsMs.Length)
                    _intervalsMs[_intervalCount++] = (float)((now - last) * 1000.0 / Stopwatch.Frequency);
                last = now;
                Wakes++;
                int hr = Fill(countUnderrun: true, now);
                if (hr < 0)
                {
                    ErrorHr = hr;
                    return;
                }
            }
        }
        finally
        {
            if (mmcss != IntPtr.Zero) NativeMethods.AvRevertMmThreadCharacteristics(mmcss);
        }
    }

    private unsafe int Fill(bool countUnderrun = false, long now = 0)
    {
        int hr = _client.GetCurrentPadding(out uint padding);
        if (hr < 0) return hr;
        if (countUnderrun && padding == 0)
        {
            Underruns++;
            if (UnderrunTimes.Count < UnderrunTimes.Capacity) UnderrunTimes.Add(now);
        }
        uint limit = QueueFrames > 0 ? Math.Min(_bufferFrames, (uint)QueueFrames) : _bufferFrames;
        if (padding >= limit) return 0;
        uint frames = limit - padding;

        var stereo = _stereo.AsSpan(0, (int)frames * 2);
        stereo.Clear();
        Source?.Invoke(stereo);

        hr = _render.GetBuffer(frames, out IntPtr data);
        if (hr < 0) return hr;
        int ch = Format.Channels;
        int bytesPerSample = Format.BlockAlign / ch;
        for (int i = 0; i < frames; i++)
        {
            float l = stereo[2 * i], r = stereo[2 * i + 1];
            for (int c = 0; c < ch; c++)
            {
                float v = ch == 1 ? 0.5f * (l + r) : c == 0 ? l : c == 1 ? r : 0f;
                int idx = i * ch + c;
                if (Format.IsFloat) ((float*)data)[idx] = v;
                else if (bytesPerSample == 2) ((short*)data)[idx] = (short)Math.Clamp(v * 32767f, -32768f, 32767f);
                else ((int*)data)[idx] = (int)Math.Clamp(v * 2147483647.0, int.MinValue, int.MaxValue);
            }
        }
        FramesWritten += frames;
        return _render.ReleaseBuffer(frames, 0);
    }

    /// <summary>Интервалы между пробуждениями, мс: среднее, медиана, 99-й перцентиль, максимум. Читать после остановки.</summary>
    public (double Mean, double P50, double P99, double Max, int Count) Intervals()
    {
        if (_intervalCount == 0) return (0, 0, 0, 0, 0);
        var copy = _intervalsMs.AsSpan(0, _intervalCount).ToArray();
        Array.Sort(copy);
        double mean = 0;
        foreach (float v in copy) mean += v;
        return (mean / copy.Length, copy[copy.Length / 2], copy[(int)Math.Min(copy.Length - 1, copy.Length * 0.99)], copy[^1], copy.Length);
    }

    public void Stop()
    {
        if (_thread is not null)
        {
            _stop = true;
            _thread.Join(2000);
            _thread = null;
        }
        if (_started)
        {
            _started = false;
            _client.Stop();
        }
    }

    public void Dispose()
    {
        Stop();
        Marshal.ReleaseComObject(_render);
        Marshal.ReleaseComObject(_client);
        _event.Dispose();
    }
}
