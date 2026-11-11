using System.Diagnostics;
using Mixer.Audio.Dsp;
using Mixer.Audio.Interop;
using Mixer.Audio.Resources;

namespace Mixer.Audio.Engine;

public enum SourceKind
{
    /// <summary>Вход устройства (микрофон, линейный вход, CABLE Output).</summary>
    DeviceInput,
    /// <summary>Звук выхода: WASAPI loopback устройства воспроизведения.</summary>
    Loopback,
    /// <summary>Звук дерева процессов: process loopback, INCLUDE_TARGET_PROCESS_TREE.</summary>
    App,
    /// <summary>Весь звук системы, кроме дерева процесса (микшера): process loopback, EXCLUDE_TARGET_PROCESS_TREE.</summary>
    System,
}

/// <summary>
/// Источник: поток захвата WASAPI (shared, event-driven). Каждый пакет сводится в стерео,
/// проходит усиление источника и пишется во все провода источника.
/// </summary>
public sealed class SourceNode : AudioNode
{
    private static readonly Wire[] NoWires = [];
    private static readonly SpscRing[] NoTaps = [];

    private volatile Wire[] _wires = NoWires;
    private volatile SpscRing[] _taps = NoTaps;
    private volatile bool _mono;
    private volatile object? _liveClient;
    private int _injectBits;
    private long _packets;
    private long _frames;
    private int _discontinuities;
    private double _captureLatencyMs;
    private long _activityTicks;

    public SourceKind Kind { get; }
    public SourceClock Clock { get; }
    /// <summary>Процесс для process loopback: цель (App) или исключаемый (System).</summary>
    public uint ProcessId { get; }
    /// <summary>Частота захвата process loopback (Windows конвертирует сама); для устройств — 0, берётся GetMixFormat.</summary>
    public int SampleRate { get; }

    public bool IsProcessLoopback => Kind is SourceKind.App or SourceKind.System;

    /// <param name="lowLatency">Только для входа устройства: loopback и process loopback идут по периоду чужого движка.</param>
    public SourceNode(string id, SourceKind kind, string endpointId, EngineOptions options, uint processId = 0, int sampleRate = 0,
        bool lowLatency = false, LowLatencyInfo? fallback = null)
        : base(id, endpointId, options, lowLatency && kind == SourceKind.DeviceInput, fallback)
    {
        Kind = kind;
        ProcessId = processId;
        SampleRate = sampleRate;
        // У приложения паузы — норма (видео на паузе, тишина в звонке): пакетов нет, провод не должен считать это сбоем.
        Clock = new SourceClock { Intermittent = IsProcessLoopback };
    }

    protected override string GoneMessage => IsProcessLoopback ? AudioStrings.Node_AppExited : base.GoneMessage;

    /// <summary>
    /// Секунд с последнего пакета (или с начала открытия, если пакетов ещё не было). Для сторожевого таймера:
    /// пакеты с флагом SILENT — тоже активность.
    /// </summary>
    public double SecondsSinceActivity
    {
        get
        {
            long t = Volatile.Read(ref _activityTicks);
            return t == 0 ? 0 : (Stopwatch.GetTimestamp() - t) / (double)Stopwatch.Frequency;
        }
    }

    protected override void OnAbandoned()
    {
        // Зависший GetNextPacketSize (этап 0) отпускает только Stop. Объект MTA, звать можно из любого MTA-потока.
        if (_liveClient is IAudioClient c) c.Stop();
    }

    public bool Mono { set => _mono = value; }

    /// <summary>Новый неизменяемый список проводов; поток захвата подхватит его со следующего пакета.</summary>
    internal void PublishWires(Wire[] wires) => _wires = wires;

    internal Wire[] Wires => _wires;

    /// <summary>Отвод для измерений: копия сигнала источника после усиления в отдельное кольцо (вне аудиоцепочки).</summary>
    public void AddTap(SpscRing ring)
    {
        var old = _taps;
        var next = new SpscRing[old.Length + 1];
        old.CopyTo(next, 0);
        next[^1] = ring;
        _taps = next;
    }

    public void RemoveTap(SpscRing ring) => _taps = Array.FindAll(_taps, t => t != ring);

    /// <summary>Тестовый импульс: добавить в первый кадр следующего пакета (для замера задержки по петле).</summary>
    public void InjectImpulse(float amplitude) => Volatile.Write(ref _injectBits, BitConverter.SingleToInt32Bits(amplitude));

    public long Packets => Interlocked.Read(ref _packets);
    public long FramesCaptured => Interlocked.Read(ref _frames);
    public int Discontinuities => Volatile.Read(ref _discontinuities);
    /// <summary>Оценка задержки захвата: GetStreamLatency + период, мс.</summary>
    public double CaptureLatencyMs => Volatile.Read(ref _captureLatencyMs);

    protected override void RunSession()
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        IAudioClient? keepAlive = null;
        IAudioRenderClient? keepAliveRender = null;
        using var evt = new EventWaitHandle(false, EventResetMode.AutoReset);
        bool started = false;
        Volatile.Write(ref _activityTicks, Stopwatch.GetTimestamp());
        try
        {
            string what = Kind switch
            {
                SourceKind.Loopback => "loopback",
                SourceKind.App => AudioStrings.Stream_AppCapture,
                SourceKind.System => AudioStrings.Stream_SystemCapture,
                _ => AudioStrings.Stream_Capture,
            };
            WaveFormat format;
            uint bufferFrames;
            if (IsProcessLoopback)
            {
                Phase("ActivateAudioInterfaceAsync (" + what + ")");
                client = ProcessLoopback.Activate(ProcessId, exclude: Kind == SourceKind.System);
                _liveClient = client;
                // GetMixFormat у такого клиента нет: формат задаётся явно, частоту конвертирует Windows (этап 0, результат 6).
                format = WaveFormat.ProcessLoopback(SampleRate > 0 ? SampleRate : 48000);
                Phase("IAudioClient.Initialize (" + what + ")");
                InitializeExplicit(client,
                    AudioConst.AUDCLNT_STREAMFLAGS_LOOPBACK | AudioConst.AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AudioConst.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
                    Options.CaptureBufferMs, format, what);
                if (client.GetBufferSize(out bufferFrames) < 0 || bufferFrames == 0)
                    bufferFrames = (uint)(format.SampleRate * Options.CaptureBufferMs / 1000);
            }
            else
            {
                Phase("IMMDevice.Activate");
                client = ActivateClient(EndpointId);
                _liveClient = client;
                uint flags = AudioConst.AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                             | (Kind == SourceKind.Loopback ? AudioConst.AUDCLNT_STREAMFLAGS_LOOPBACK : 0);
                Phase("IAudioClient.Initialize (" + what + ")");
                format = OpenShared(ref client, flags, Options.CaptureBufferMs, what);
                _liveClient = client;
                AudioException.Check(client!.GetBufferSize(out bufferFrames), what, "GetBufferSize");
            }
            capture = GetService<IAudioCaptureClient>(client, what);
            AudioException.Check(client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle()), what, "SetEventHandle");
            if (client.GetStreamLatency(out long latency100ns) < 0) latency100ns = 0;
            double period = StreamPeriodSeconds(format.SampleRate);
            Volatile.Write(ref _captureLatencyMs, latency100ns / 10_000.0 + (period > 0 ? period * 1000 : Options.PeriodMs));

            uint keepAliveFrames = 0;
            if (Kind == SourceKind.Loopback)
            {
                // Loopback молчит, пока в устройство никто не играет: пакетов нет, провода бы опустели.
                // Держим устройство активным потоком тишины (тот же приём, что у OBS).
                string silent = AudioStrings.Stream_KeepAlive;
                Phase("IAudioClient.Initialize (" + silent + ")");
                keepAlive = ActivateClient(EndpointId);
                InitializeShared(keepAlive, 0, 100, silent);
                AudioException.Check(keepAlive.GetBufferSize(out keepAliveFrames), AudioStrings.Stream_KeepAliveBufferSize);
                keepAliveRender = GetService<IAudioRenderClient>(keepAlive, silent);
                FillSilence(keepAlive, keepAliveRender, keepAliveFrames);
                AudioException.Check(keepAlive.Start(), AudioStrings.Stream_KeepAliveStart);
            }

            var map = new ChannelMap(format);
            int maxFrames = (int)Math.Max(bufferFrames, 4096);
            var buf = new float[maxFrames * 2];
            var gain = new StereoGain();
            gain.Init(format.SampleRate, GainL, GainR, Muted);
            SetFormat(format);

            Phase("IAudioClient.Start (" + what + ")");
            AudioException.Check(client.Start(), what, "Start");
            started = true;
            Clock.Started(format.SampleRate, period);
            SetState(NodeState.Running);

            long sessionPackets = 0;
            while (!StopRequested)
            {
                evt.WaitOne(100);
                if (StopRequested) break;
                if (keepAlive is not null) FillSilence(keepAlive, keepAliveRender!, keepAliveFrames);

                AudioException.Check(capture.GetNextPacketSize(out uint packet), what, "GetNextPacketSize");
                while (packet > 0)
                {
                    AudioException.Check(capture.GetBuffer(out IntPtr data, out uint frames, out uint bufFlags, out _, out _), what, "GetBuffer");
                    long now = Stopwatch.GetTimestamp();
                    Volatile.Write(ref _activityTicks, now);
                    int done = 0;
                    while (done < frames)
                    {
                        int n = Math.Min((int)frames - done, maxFrames);
                        IntPtr chunk = data + done * format.BlockAlign;
                        ProcessPacket(map, chunk, n, bufFlags, buf, ref gain, now, format.SampleRate);
                        done += n;
                    }
                    AudioException.Check(capture.ReleaseBuffer(frames), what, "ReleaseBuffer");
                    Interlocked.Increment(ref _packets);
                    Interlocked.Add(ref _frames, frames);
                    sessionPackets++;
                    if ((bufFlags & AudioConst.AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0)
                    {
                        Interlocked.Increment(ref _discontinuities);
                        Clock.Break();
                        // разрыв на первом пакете — норма запуска потока, не сбой
                        if (sessionPackets > 1) CountGlitch(now);
                    }
                    AudioException.Check(capture.GetNextPacketSize(out packet), what, "GetNextPacketSize");
                }
            }
        }
        finally
        {
            _liveClient = null;
            Clock.Stopped();
            if (started) client!.Stop();
            keepAlive?.Stop();
            Release(keepAliveRender);
            Release(keepAlive);
            Release(capture);
            Release(client);
        }
    }

    private void ProcessPacket(ChannelMap map, IntPtr data, int frames, uint flags, float[] buf, ref StereoGain gain, long now, int rate)
    {
        var span = buf.AsSpan(0, frames * 2);
        if ((flags & AudioConst.AUDCLNT_BUFFERFLAGS_SILENT) != 0) span.Clear();
        else map.Decode(data, frames, span);

        // NaN/Inf от кривого драйвера отравили бы ресемплеры всех проводов
        for (int i = 0; i < span.Length; i++)
            if (!float.IsFinite(span[i])) span[i] = 0f;

        int inject = Volatile.Read(ref _injectBits);
        if (inject != 0)
        {
            Volatile.Write(ref _injectBits, 0);
            float a = BitConverter.Int32BitsToSingle(inject);
            span[0] += a;
            span[1] += a;
        }

        if (_mono)
        {
            for (int i = 0; i < span.Length; i += 2)
            {
                float m = 0.5f * (span[i] + span[i + 1]);
                span[i] = m;
                span[i + 1] = m;
            }
        }

        Chain.Process(span, frames, rate);
        gain.SetTargets(GainL, GainR, Muted);
        gain.Apply(span, frames);
        Peak.Update(span, frames);

        var wires = _wires;
        for (int i = 0; i < wires.Length; i++) wires[i].Write(span, frames, now);
        var taps = _taps;
        for (int i = 0; i < taps.Length; i++) taps[i].Write(span, frames);
    }

    private static void FillSilence(IAudioClient client, IAudioRenderClient render, uint bufferFrames)
    {
        if (client.GetCurrentPadding(out uint padding) < 0) return;
        uint n = bufferFrames - padding;
        if (n == 0) return;
        if (render.GetBuffer(n, out _) < 0) return;
        render.ReleaseBuffer(n, AudioConst.AUDCLNT_BUFFERFLAGS_SILENT);
    }
}
