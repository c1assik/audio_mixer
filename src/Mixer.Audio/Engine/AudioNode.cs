using System.Diagnostics;
using System.Runtime.InteropServices;
using Mixer.Audio.Dsp;
using Mixer.Audio.Interop;
using Mixer.Audio.Resources;

namespace Mixer.Audio.Engine;

public enum NodeState { Starting, Running, WaitingDevice, Error, Stopped }

/// <summary>
/// Общая часть узла с устройством: свой поток (MTA, MMCSS «Pro Audio»), цикл «открыть → работать → при сбое подождать и повторить».
/// Все вызовы COM устройства идут только из этого потока.
/// </summary>
public abstract class AudioNode : IDisposable
{
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;
    private volatile NodeState _state = NodeState.Stopped;
    private volatile string? _message;
    private volatile WaveFormat? _format;
    private volatile string? _phase;
    private long _phaseTicks;

    /// <summary>Вызов открытия устройства дольше этого считается зависшим (служба аудио нездорова).</summary>
    /// <summary>USB-микрофон на этой машине открывается 4–5 с, поэтому порог с запасом.</summary>
    public const double OpenTimeoutSeconds = 8;

    // параметры от управляющего потока
    private volatile float _gainL = 1f, _gainR = 1f;
    private volatile bool _muted;

    protected EngineOptions Options { get; }
    protected volatile bool StopRequested;

    public string Id { get; }
    public string EndpointId { get; }
    /// <summary>
    /// Состояние узла. Если открытие устройства висит дольше OpenTimeoutSeconds, узел показывает ошибку:
    /// прервать зависший вызов WASAPI нельзя, но интерфейс должен знать, что звука не будет.
    /// </summary>
    public NodeState State => IsStuck ? NodeState.Error : _state;
    /// <summary>Пояснение к состоянию: текст ошибки или «устройство отключено».</summary>
    public string? Message => IsStuck
        ? Loc.F(AudioStrings.Node_StuckFormat, _phase, StuckSeconds)
        : _message;

    private double StuckSeconds => (Stopwatch.GetTimestamp() - Volatile.Read(ref _phaseTicks)) / (double)Stopwatch.Frequency;
    private bool IsStuck => _state == NodeState.Starting && _phase is not null && StuckSeconds > OpenTimeoutSeconds;

    /// <summary>Отметить начало блокирующего вызова при открытии устройства (null — открытие закончено).</summary>
    protected void Phase(string? name)
    {
        Volatile.Write(ref _phaseTicks, Stopwatch.GetTimestamp());
        _phase = name;
    }
    /// <summary>Формат устройства (после успешного открытия).</summary>
    public WaveFormat? Format => _format;
    public PeakMeter Peak { get; } = new();
    /// <summary>Обработка узла (гейт → EQ → компрессор) перед фейдером.</summary>
    public ProcessingChain Chain { get; } = new();

    /// <summary>Сколько раз поток открывал устройство заново после сбоя.</summary>
    public int Restarts { get; private set; }

    protected AudioNode(string id, string endpointId, EngineOptions options)
    {
        Id = id;
        EndpointId = endpointId;
        Options = options;
    }

    public void SetGain(float gainDb, float balance, bool muted)
    {
        var (l, r) = StereoGain.Targets(gainDb, balance);
        _gainL = l;
        _gainR = r;
        _muted = muted;
    }

    protected float GainL => _gainL;
    protected float GainR => _gainR;
    protected bool Muted => _muted;

    public void Start()
    {
        if (_thread is not null) return;
        _state = NodeState.Starting;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = $"{GetType().Name} {Id}", Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Попросить поток выйти, не дожидаясь его (чтобы несколько узлов останавливались параллельно).</summary>
    public void RequestStop()
    {
        if (_thread is null) return;
        _stop = true;
        StopRequested = true;
        _wake.Set();
    }

    /// <summary>
    /// Остановить поток. Если он не вышел за timeoutMs (зависший вызов WASAPI), бросаем его: поток фоновый.
    /// Брошенному узлу даётся шанс выйти: <see cref="OnAbandoned"/> из пула потоков (на этапе 0 зависший
    /// GetNextPacketSize process loopback отпускал только IAudioClient.Stop).
    /// </summary>
    public bool Stop(int timeoutMs = 2000)
    {
        var t = _thread;
        if (t is null) return true;
        RequestStop();
        bool joined = t.Join(timeoutMs);
        _thread = null;
        _state = NodeState.Stopped;
        if (!joined)
        {
            Abandoned = true;
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try { OnAbandoned(); }
                catch (Exception) { /* объект мог освободиться, пока поток выходил */ }
            }, null);
        }
        return joined;
    }

    /// <summary>Поток узла не вышел при остановке и брошен.</summary>
    public bool Abandoned { get; private set; }

    /// <summary>Разблокировать зависший вызов брошенного потока (вызывается из пула потоков, MTA).</summary>
    protected virtual void OnAbandoned()
    {
    }

    /// <summary>Пояснение, когда устройство занято другим приложением в эксклюзивном режиме.</summary>
    public static string BusyMessage => AudioStrings.Node_Busy;

    /// <summary>Пояснение, когда устройство (или процесс) пропало.</summary>
    protected virtual string GoneMessage => AudioStrings.Node_Gone;

    public void Dispose() => Stop();

    private void ThreadMain()
    {
        uint taskIndex = 0;
        IntPtr mmcss = Options.UseMmcss ? NativeMethods.AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex) : IntPtr.Zero;
        try
        {
            bool first = true;
            while (!_stop)
            {
                if (!first) Restarts++;
                first = false;
                try
                {
                    _state = NodeState.Starting;
                    Phase(AudioStrings.Phase_Opening);
                    RunSession();
                    if (_stop) break;
                    // сеанс закончился сам (устройство ушло без исключения)
                    SetState(NodeState.WaitingDevice, GoneMessage);
                }
                catch (AudioException ex) when (ex.IsDeviceGone)
                {
                    SetState(NodeState.WaitingDevice, GoneMessage);
                }
                catch (AudioException ex) when (ex.IsDeviceInUse)
                {
                    // эксклюзивный режим у другого приложения: не ошибка, повтор по таймеру
                    SetState(NodeState.WaitingDevice, BusyMessage);
                }
                catch (DeviceUnavailableException ex)
                {
                    SetState(NodeState.WaitingDevice, ex.Message);
                }
                catch (Exception ex) when (ex is AudioException or COMException or InvalidCastException)
                {
                    SetState(NodeState.Error, ex.Message);
                }
                if (_stop) break;
                _wake.WaitOne(Options.RetryDelayMs);
            }
        }
        finally
        {
            if (mmcss != IntPtr.Zero) NativeMethods.AvRevertMmThreadCharacteristics(mmcss);
            _state = NodeState.Stopped;
        }
    }

    protected void SetState(NodeState state, string? message = null)
    {
        if (state != NodeState.Starting) _phase = null;
        _message = message;
        _state = state;
    }

    protected void SetFormat(WaveFormat format) => _format = format;

    /// <summary>Открыть устройство и работать, пока не попросят остановиться или пока не случится ошибка.</summary>
    protected abstract void RunSession();

    // ── помощники COM ──

    internal static IAudioClient ActivateClient(string endpointId)
    {
        var enumerator = DeviceCatalog.CreateEnumerator();
        try
        {
            int hr = enumerator.GetDevice(endpointId, out var device);
            if (hr < 0) throw new DeviceUnavailableException(AudioStrings.Node_NotFound);
            try
            {
                if (device.GetState(out uint st) < 0 || st != AudioConst.DEVICE_STATE_ACTIVE)
                    throw new DeviceUnavailableException(AudioStrings.Node_Gone);
                Guid iid = typeof(IAudioClient).GUID;
                AudioException.Check(device.Activate(ref iid, AudioConst.CLSCTX_ALL, IntPtr.Zero, out object client), "IMMDevice.Activate(IAudioClient)");
                return (IAudioClient)client;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>GetMixFormat + Initialize (shared). Возвращает формат микса.</summary>
    internal static WaveFormat InitializeShared(IAudioClient client, uint flags, double bufferMs, string what)
    {
        AudioException.Check(client.GetMixFormat(out IntPtr pMix), what, "GetMixFormat");
        try
        {
            var format = WaveFormat.FromPointer(pMix);
            if (format.Type == SampleType.Unsupported)
                throw new AudioException(Loc.F(AudioStrings.Node_UnsupportedFormatFormat, what, format));
            long hns = (long)(bufferMs * 10_000);
            AudioException.Check(client.Initialize(AudioConst.AUDCLNT_SHAREMODE_SHARED, flags, hns, 0, pMix, IntPtr.Zero), what, "Initialize");
            return format;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pMix);
        }
    }

    /// <summary>Initialize (shared) с заданным форматом — для process loopback, где GetMixFormat недоступен.</summary>
    internal static void InitializeExplicit(IAudioClient client, uint flags, double bufferMs, WaveFormat format, string what)
    {
        IntPtr p = format.AllocCoTaskMem();
        try
        {
            long hns = (long)(bufferMs * 10_000);
            AudioException.Check(client.Initialize(AudioConst.AUDCLNT_SHAREMODE_SHARED, flags, hns, 0, p, IntPtr.Zero), what, "Initialize");
        }
        finally
        {
            Marshal.FreeCoTaskMem(p);
        }
    }

    internal static T GetService<T>(IAudioClient client, string what) where T : class
    {
        Guid iid = typeof(T).GUID;
        AudioException.Check(client.GetService(ref iid, out object svc), what, "GetService(" + typeof(T).Name + ")");
        return (T)svc;
    }

    internal static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }
}

/// <summary>Устройство отсутствует или выключено: узел ждёт, это не ошибка.</summary>
public sealed class DeviceUnavailableException(string message) : Exception(message);
