using System.Diagnostics;
using Mixer.Audio.Dsp;
using Mixer.Audio.Interop;
using Mixer.Audio.Resources;

namespace Mixer.Audio.Engine;

/// <summary>
/// Выход: свой рендер-цикл WASAPI (shared, event-driven) по часам своего устройства.
/// На каждый период: читает все входящие провода (каждый через свой ресемплер), складывает,
/// применяет усиление выхода и ограничитель, пишет в устройство.
/// </summary>
public sealed class OutputNode : AudioNode
{
    // строка читается при создании узла: в цикле выхода нет обращений к ResourceManager
    private readonly string What = AudioStrings.Stream_Playback;
    private volatile MixPlan _plan = MixPlan.Empty;
    private int _starvations;
    private int _eventTimeouts;
    private int _limiterBlocks;
    private float _limiterMinGain = 1f;
    private long _framesRendered;
    private double _renderLatencyMs;
    private int _bufferFrames;
    private int _queueFrames;

    public OutputNode(string id, string endpointId, EngineOptions options) : base(id, endpointId, options)
    {
    }

    /// <summary>Новый план сведения; поток выхода подхватит его со следующего периода.</summary>
    internal void PublishPlan(MixPlan plan) => _plan = plan;

    internal MixPlan Plan => _plan;

    /// <summary>Сколько раз к нашему пробуждению буфер устройства был уже пуст (вероятный щелчок на выходе).</summary>
    public int Starvations => Volatile.Read(ref _starvations);
    public int EventTimeouts => Volatile.Read(ref _eventTimeouts);
    /// <summary>Блоков, в которых срабатывал ограничитель.</summary>
    public int LimiterBlocks => Volatile.Read(ref _limiterBlocks);
    public long FramesRendered => Interlocked.Read(ref _framesRendered);
    /// <summary>Оценка задержки выхода: очередь в буфере устройства + GetStreamLatency, мс.</summary>
    public double RenderLatencyMs => Volatile.Read(ref _renderLatencyMs);
    /// <summary>Размер буфера WASAPI, который выделила Windows.</summary>
    public int BufferFrames => Volatile.Read(ref _bufferFrames);
    /// <summary>До скольких кадров движок заполняет буфер (очередь в устройстве).</summary>
    public int QueueFrames => Volatile.Read(ref _queueFrames);

    /// <summary>Минимальное усиление ограничителя с прошлого опроса (1 — не срабатывал).</summary>
    public float TakeLimiterMinGain() => Interlocked.Exchange(ref _limiterMinGain, 1f);

    protected override void RunSession()
    {
        IAudioClient? client = null;
        IAudioRenderClient? render = null;
        using var evt = new EventWaitHandle(false, EventResetMode.AutoReset);
        bool started = false;
        try
        {
            Phase("IMMDevice.Activate");
            client = ActivateClient(EndpointId);
            Phase("IAudioClient.Initialize (" + What + ")");
            var format = InitializeShared(client, AudioConst.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, Options.RenderBufferMs, What);
            AudioException.Check(client.GetBufferSize(out uint bufferFrames), What, "GetBufferSize");
            render = GetService<IAudioRenderClient>(client, What);
            AudioException.Check(client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle()), What, "SetEventHandle");
            client.GetStreamLatency(out long latency100ns);
            double streamLatencyMs = latency100ns / 10_000.0;
            Volatile.Write(ref _bufferFrames, (int)bufferFrames);

            int rate = format.SampleRate;
            // Windows часто выделяет буфер больше запрошенного (22 мс вместо 10). Держать его полным — лишняя задержка,
            // поэтому очередь в устройстве ограничена: период + запас.
            uint queueFrames = Math.Min(bufferFrames, (uint)Math.Ceiling(rate * Math.Max(Options.RenderQueueMs, Options.PeriodMs) / 1000.0));
            Volatile.Write(ref _queueFrames, (int)queueFrames);
            var map = new ChannelMap(format);
            var mix = new float[bufferFrames * 2];
            var scratch = new float[bufferFrames * 2];
            var gain = new StereoGain();
            gain.Init(rate, GainL, GainR, Muted);
            var limiter = new Limiter();
            limiter.Init(rate);
            SetFormat(format);
            SincTable.Prewarm([rate, 44100, 48000]);

            // Буфер сначала заполняется тишиной, чтобы первый период не начался с пустоты.
            AudioException.Check(render.GetBuffer(queueFrames, out _), What, "GetBuffer");
            AudioException.Check(render.ReleaseBuffer(queueFrames, AudioConst.AUDCLNT_BUFFERFLAGS_SILENT), What, "ReleaseBuffer");
            Phase("IAudioClient.Start (" + What + ")");
            AudioException.Check(client.Start(), What, "Start");
            started = true;
            SetState(NodeState.Running);

            long cycles = 0;
            while (!StopRequested)
            {
                if (!evt.WaitOne(200)) Interlocked.Increment(ref _eventTimeouts);
                if (StopRequested) break;

                AudioException.Check(client.GetCurrentPadding(out uint padding), What, "GetCurrentPadding");
                int n = (int)queueFrames - (int)padding;
                if (n <= 0) continue;
                if (padding == 0 && cycles > 4) Interlocked.Increment(ref _starvations);
                cycles++;

                long now = Stopwatch.GetTimestamp();
                var block = mix.AsSpan(0, n * 2);
                block.Clear();
                _plan.Render(block, scratch, n, rate, now);

                Chain.Process(block, n, rate);
                gain.SetTargets(GainL, GainR, Muted);
                gain.Apply(block, n);
                float minGain = limiter.Process(block, n);
                if (minGain < 0.999f)
                {
                    Interlocked.Increment(ref _limiterBlocks);
                    float cur = Volatile.Read(ref _limiterMinGain);
                    if (minGain < cur) Interlocked.CompareExchange(ref _limiterMinGain, minGain, cur);
                }
                Peak.Update(block, n);

                AudioException.Check(render.GetBuffer((uint)n, out IntPtr data), What, "GetBuffer");
                map.Encode(block, n, data);
                AudioException.Check(render.ReleaseBuffer((uint)n, 0), What, "ReleaseBuffer");

                Interlocked.Add(ref _framesRendered, n);
                Volatile.Write(ref _renderLatencyMs, (padding + n) * 1000.0 / rate + streamLatencyMs);
            }
        }
        finally
        {
            foreach (var w in _plan.AllWires) w.Deactivate();
            if (started) client!.Stop();
            Release(render);
            Release(client);
        }
    }
}
