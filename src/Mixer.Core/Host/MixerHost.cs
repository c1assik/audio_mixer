using System.Collections.Concurrent;
using System.Diagnostics;
using Mixer.Audio.Engine;
using Mixer.Audio.Interop;
using Mixer.Core.Apps;
using Mixer.Core.Cables;
using Mixer.Core.Config;
using Mixer.Core.Graph;
using Mixer.Core.Sys;

namespace Mixer.Core.Host;

public sealed record MixerHostOptions
{
    /// <summary>Такт наблюдателя процессов, с. Спецификация требует сброс привязки в течение 3 с после запуска приложения.</summary>
    public double TickSeconds { get; init; } = 1.0;
    public bool AllowFeedback { get; init; }
    /// <summary>Не трогать привязки приложений вовсе (диагностика: только захват).</summary>
    public bool DisableTakeOver { get; init; }
    /// <summary>Устройство вернулось или пропало: реагировать через столько секунд тишины в событиях (защита от дребезга).</summary>
    public double DeviceDebounceSeconds { get; init; } = 0.5;
    /// <summary>После пробуждения пересоздавать потоки через столько секунд: служба аудио и USB должны проснуться.</summary>
    public double ResumeDelaySeconds { get; init; } = 1.0;
    /// <summary>Опрос списка устройств, если Windows не приняла подписку IMMNotificationClient, с.</summary>
    public double DevicePollSeconds { get; init; } = 3.0;
}

/// <summary>Неизменяемый снимок состояния для интерфейса (читается из любого потока).</summary>
public sealed record HostStatus
{
    public BuildResult? Build { get; init; }
    public string? ProcessLoopbackError { get; init; }
    public string? PolicyError { get; init; }
    public SinkCheck? Sink { get; init; }
    public IReadOnlyDictionary<string, AppBinding> Bindings { get; init; } = new Dictionary<string, AppBinding>();
    /// <summary>Перезапуски сторожевым таймером по ID источника в движке.</summary>
    public IReadOnlyDictionary<string, int> WatchdogRestarts { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<string> BoundApps { get; init; } = [];
    /// <summary>Устройства на момент последней сборки графа (для проверки проводов в интерфейсе).</summary>
    public IReadOnlyList<EndpointInfo> Devices { get; init; } = [];
    /// <summary>Граф, по которому собран <see cref="Build"/> (интерфейс мог уже уйти вперёд).</summary>
    public GraphConfig? Graph { get; init; }
    /// <summary>Подписка на изменения устройств не работает (null — работает): устройства опрашиваются по таймеру.</summary>
    public string? DeviceWatchError { get; init; }
    /// <summary>Индикаторы узлов на прямом маршруте (по ID узла): пики сессий приложения.</summary>
    public IReadOnlyDictionary<string, Mixer.Audio.Dsp.PeakMeter> DirectMeters { get; init; } = new Dictionary<string, Mixer.Audio.Dsp.PeakMeter>();
    /// <summary>Сколько сессий приложения на выходе прямого маршрута (по ID узла).</summary>
    public IReadOnlyDictionary<string, int> DirectSessions { get; init; } = new Dictionary<string, int>();
    /// <summary>Ошибки громкости прямого маршрута по exe.</summary>
    public IReadOnlyDictionary<string, string> DirectErrors { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> VolumeApps { get; init; } = [];
    /// <summary>exe процессов с аудиосессией (кроме микшера) на последнем такте: кандидаты в исключения «Весь звук системы».</summary>
    public IReadOnlyList<string> PlayingApps { get; init; } = [];
    /// <summary>Назначения кабеля микрофоном (micFor) по exe: Bound — приложению назначен выход кабеля.</summary>
    public IReadOnlyDictionary<string, AppBinding> MicBindings { get; init; } = new Dictionary<string, AppBinding>();
    public IReadOnlyList<string> MicApps { get; init; } = [];
    /// <summary>Предупреждения узлов-кабелей по ID узла (<see cref="CableChecks"/>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> CableWarnings { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>
/// Управляющий слой микшера: движок + наблюдатель процессов + «Забрать себе» + проверка заглушки + сторожевой таймер.
/// Всё, что трогает COM (кроме аудиопотоков), идёт в одном собственном MTA-потоке, никогда в потоке интерфейса.
/// Журнал boundApps принадлежит хосту: граф от интерфейса принимается без него, а сохраняется вместе с ним.
/// </summary>
public sealed class MixerHost : IDisposable
{
    private readonly MixerHostOptions _opt;
    private readonly Action<GraphConfig>? _save;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly CaptureWatchdog _watchdog = new();
    private readonly Dictionary<string, int> _restarts = new();
    private readonly Dictionary<string, int?> _rateCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Thread? _thread;
    private volatile bool _disposed;

    // ── состояние управляющего потока ──
    private GraphConfig _graph;
    private List<EndpointInfo> _devices = [];
    private string? _plError;
    private WindowsEndpointPolicy? _policy;
    private string? _policyError;
    private TakeOverManager? _takeOver;
    /// <summary>Назначение кабеля микрофоном приложениям (micFor): тот же механизм, политика устройства записи.</summary>
    private TakeOverManager? _mic;
    private Dictionary<string, AppBinding> _lastMic = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AudioSessionInfo>? _captureSessions;
    private Dictionary<string, IReadOnlyList<string>> _cableWarnings = new();
    private SinkCheck? _sink;
    private string? _sinkCheckedFor;
    private BuildResult? _build;
    private string _rootsKey = "";
    private AppSnapshot? _world;
    /// <summary>Последняя проверка кандидата в заглушки из настроек: годна, пока свежая.</summary>
    private (string Id, SinkCheck Result, double At)? _probed;
    private GraphConfig? _pendingGraph;
    private volatile HostStatus _status = new();

    // ── горячая замена, сон ──
    private DeviceWatcher? _watcher;
    private string? _watchError;
    private PowerEvents? _power;
    private readonly SleepDetector _sleep = new();
    private readonly Debounce _deviceEvents;
    private readonly Debounce _resume;
    private readonly HashSet<string> _formatChanged = new(StringComparer.OrdinalIgnoreCase);
    private double _lastResume = double.NegativeInfinity;
    private double _nextDevicePoll;
    private readonly Dictionary<string, (NodeState State, string? Message)> _nodeStates = new();
    private readonly Dictionary<string, LowLatencyInfo> _lowLatencyStates = new();

    // ── прямые маршруты, новые сессии ──
    private DirectVolumes? _direct;
    private SessionNotifier? _notifier;
    private string _notifierKey = "";
    private int _sessionPending;
    /// <summary>Stopwatch-отметка первого уведомления из текущей пачки (для журнала: сколько ушло до захвата).</summary>
    private long _sessionAt;
    private double _meterDue;
    /// <summary>Цели привязок, поставленные последним полным сведением (exe → заглушка или выход).</summary>
    private Dictionary<string, string?> _lastDesired = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Индикатор сессии опрашивается с частотой кадров интерфейса.</summary>
    private const double MeterPeriod = 1.0 / 30;

    public AudioEngine Engine { get; }
    public HostStatus Status => _status;

    /// <summary>События для журнала (из управляющего потока).</summary>
    public event Action<string>? Log;

    /// <param name="save">Атомарная запись конфига (граф + журнал). Вызывается до каждой установки привязки.</param>
    public MixerHost(GraphConfig graph, Action<GraphConfig>? save, EngineOptions? engineOptions = null, MixerHostOptions? options = null)
    {
        _graph = graph;
        _save = save;
        _opt = options ?? new MixerHostOptions();
        Engine = new AudioEngine(engineOptions);
        _deviceEvents = new Debounce(_opt.DeviceDebounceSeconds, Math.Max(2.0, _opt.DeviceDebounceSeconds * 4));
        _resume = new Debounce(_opt.ResumeDelaySeconds, _opt.ResumeDelaySeconds * 3);
    }

    /// <summary>Текущий граф вместе с журналом привязок.</summary>
    public GraphConfig Graph => Volatile.Read(ref _graph);

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "MixerHost" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Дождаться первого применения графа.</summary>
    public bool WaitReady(TimeSpan timeout) => _ready.Wait(timeout);

    /// <summary>
    /// Новый граф от интерфейса. Журнал boundApps берётся у хоста. Правки склеиваются: пока управляющий поток занят,
    /// применится только последний граф (фейдер шлёт десятки правок в секунду). Сколько работы делать, решает
    /// <see cref="GraphChange"/>: позиции не трогают движок, усиления не перечитывают устройства и процессы.
    /// </summary>
    public void Update(GraphConfig graph)
    {
        if (Interlocked.Exchange(ref _pendingGraph, graph) is null) Post(ApplyPending);
    }

    private void ApplyPending()
    {
        var next = Interlocked.Exchange(ref _pendingGraph, null);
        if (next is null) return;
        var prev = _graph;
        Volatile.Write(ref _graph, next with { BoundApps = prev.BoundApps, VolumeApps = prev.VolumeApps, MicApps = prev.MicApps });
        var change = GraphChange.Classify(prev, _graph);
        if (change is GraphChangeKind.None or GraphChangeKind.Layout)
        {
            Publish();
            return;
        }
        AppSnapshot world;
        if (change == GraphChangeKind.Structure)
        {
            _devices = DeviceCatalog.ListAll();
            _rateCache.Clear();
            world = TakeWorld();
        }
        else
        {
            world = _world ?? TakeWorld();
        }
        // привязки («Забрать себе», прямой маршрут) и громкость сессий меняются в Rebuild сразу, а не на следующем такте
        Rebuild(world, "graph changed", reopenSessions: change == GraphChangeKind.Structure);
        Publish();
    }

    /// <summary>Выполнить в управляющем потоке (COM не из потока интерфейса).</summary>
    public Task<T> RunAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_disposed)
        {
            tcs.SetCanceled();
            return tcs.Task;
        }
        _queue.Add(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>Данные палитры «Добавить узел»: устройства, приложения со звуком и с окнами.</summary>
    public Task<SourceCatalog> QueryCatalogAsync() => RunAsync(() => ReadCatalog(_plError));

    /// <summary>Каталог без хоста (интерфейс без звука): вызывать не из потока интерфейса.</summary>
    public static SourceCatalog ReadCatalog(string? processLoopbackError)
    {
        var devices = DeviceCatalog.ListAll();
        var procs = ProcessSnapshot.Take();
        var sessions = AudioSessions.List(devices.Where(d => d.Flow == EDataFlow.Render).Select(d => d.Id));
        var apps = AppCatalog.Build(procs, sessions, ProcessDetails.WindowOwners(), ProcessDetails.TryGetPath, ProcessDetails.Describe);
        return new SourceCatalog(devices, apps, processLoopbackError, SinkValidator.IsVoicemeeterRunning(procs));
    }

    /// <summary>
    /// Проверить устройство как заглушку до того, как её назначить: пробный поток с таймаутом в управляющем потоке.
    /// Годный результат запоминается, и при назначении эта же заглушка второй раз не проверяется.
    /// </summary>
    public Task<SinkCheck> ProbeSinkAsync(string endpointId) => RunAsync(() =>
    {
        _devices = DeviceCatalog.ListAll();
        var procs = ProcessSnapshot.Take();
        var r = SinkValidator.Check(endpointId, _devices, procs, ep => StreamProbe.TryRender(ep, SinkValidator.ProbeTimeout), MixChannels);
        _probed = (endpointId, r, _clock.Elapsed.TotalSeconds);
        Emit(r.Usable ? $"sink \"{r.Endpoint?.Name}\" checked" : "sink rejected: " + r.Message);
        return r;
    });

    /// <summary>
    /// Проверить заглушку заново (запустили Voicemeeter, подключили устройство). При изменении графа проверка
    /// повторяется, только если сменилось само устройство: пробный поток на каждое движение фейдера не нужен.
    /// </summary>
    public void RecheckSink() => Post(() =>
    {
        _sink = null;
        _devices = DeviceCatalog.ListAll();
        Rebuild(TakeWorld(), "sink rechecked");
    });

    private void Post(Action a)
    {
        if (!_disposed) _queue.Add(a);
    }

    private void Emit(string message) => Log?.Invoke(message);

    private void ThreadMain()
    {
        try
        {
            Init();
        }
        catch (Exception ex)
        {
            Emit("start failed: " + ex.Message);
        }
        finally
        {
            _ready.Set();
        }

        while (!_disposed)
        {
            double nextTick = Now + _opt.TickSeconds;
            while (!_disposed)
            {
                Guard(RunDue);
                double now = Now;
                if (now >= nextTick) break;
                double wake = Math.Min(nextTick, Math.Min(_deviceEvents.Due ?? double.MaxValue, _resume.Due ?? double.MaxValue));
                if (_direct?.Any == true) wake = Math.Min(wake, _meterDue);
                int left = (int)Math.Clamp(Math.Ceiling((wake - now) * 1000), 1, int.MaxValue);
                if (_queue.TryTake(out var action, left)) Guard(action);
            }
            if (_disposed) break;
            Guard(Tick);
        }
        while (_queue.TryTake(out var rest)) Guard(rest);
    }

    private void Guard(Action a)
    {
        try
        {
            a();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // управляющий поток не должен умирать: ошибка такта повторится или пройдёт на следующем
            Emit($"control thread error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>Отложенная работа, срок которой подошёл: пробуждение, изменения устройств.</summary>
    private void RunDue()
    {
        double now = Now;
        if (_resume.TryFire(now)) Resume();
        if (_deviceEvents.TryFire(now)) RefreshDevices("devices changed");
        if (_direct?.Any == true && now >= _meterDue)
        {
            _direct.PollMeters();
            _meterDue = now + MeterPeriod;
        }
    }

    private void Init()
    {
        _watcher = DeviceWatcher.TryCreate(out _watchError);
        if (_watcher is not null) _watcher.Changed += OnDeviceChange;
        else Emit($"device change notifications don't work ({_watchError}): polling the device list every {_opt.DevicePollSeconds:0} s");
        _power = PowerEvents.TryCreate(out string? powerError);
        if (_power is not null) _power.Changed += suspending => Post(() => OnPower(suspending));
        else Emit($"sleep notifications unavailable ({powerError}): wake-up is detected by the clock");

        _devices = DeviceCatalog.ListAll();
        _plError = ProcessLoopback.Check();
        if (_plError is not null) Emit(_plError);

        if (!_opt.DisableTakeOver)
        {
            _policy = WindowsEndpointPolicy.TryCreate(out _policyError);
            if (_policy is not null)
            {
                _takeOver = new TakeOverManager(_policy, new Journal(this));
                _direct = new DirectVolumes(AppSession.Open, new VolumeJournal(this));
                _mic = new TakeOverManager(_policy.ForCapture(), new MicJournal(this), checkPlayback: false);
            }
            else
                Emit("Take over unavailable: " + _policyError + ". Fallback: pick the app's device in the Windows volume mixer");
        }

        var world = TakeWorld();
        if (_graph.BoundApps.Count > 0)
            Emit($"redirect journal from the last run: {string.Join(", ", _graph.BoundApps)}");
        if (_graph.VolumeApps.Count > 0)
            Emit($"session volume journal from the last run: {string.Join(", ", _graph.VolumeApps)}");
        if (_graph.MicApps.Count > 0)
            Emit($"microphone assignments: {string.Join(", ", _graph.MicApps)}");
        CheckSink(world);
        Rebuild(world, "start", reopenSessions: true);
        EnsureNotifier();
        Publish();
    }

    /// <summary>Процессы и сессии нужны, только если в графе есть приложения или в журнале остались привязки.</summary>
    private bool NeedsWorld =>
        _graph.BoundApps.Count > 0 || _graph.VolumeApps.Count > 0 || _graph.MicApps.Count > 0
        || _graph.Nodes.Any(n => n.Kind is NodeKind.App or NodeKind.System || n.MicFor is { Count: > 0 }) || UsesVoicemeeter || HasCableMics;

    /// <summary>На схеме есть кабель-микрофон: по сессиям проверяется, не пишет ли в него кто-то ещё.</summary>
    private bool HasCableMics => _graph.Nodes.Any(n => n.Kind == NodeKind.DeviceOutput && CableTable.Default.LinkOf(n)?.Role == CableRole.VirtualMic);

    /// <summary>Узлы на эндпоинтах Voicemeeter ждут его запуска — за процессами надо следить.</summary>
    private bool UsesVoicemeeter =>
        _graph.Nodes.Any(n => n.Kind is NodeKind.DeviceInput or NodeKind.Loopback or NodeKind.DeviceOutput
                              && GraphBuilder.Resolve(n, _devices) is { IsVoicemeeter: true });

    private void Tick()
    {
        double slept = _sleep.SleptSeconds();
        if (slept > 0 && Now - _lastResume > 5 && !_resume.Pending)
        {
            Emit($"by the clock the system slept {slept:F0} s: streams will be recreated");
            _resume.Hit(Now);
        }
        LogNodeStates();
        if (_watcher is null && Now >= _nextDevicePoll)
        {
            _nextDevicePoll = Now + _opt.DevicePollSeconds;
            RefreshDevices("devices changed (poll)");
        }

        // Снимок процессов и сессий аллоцирует (~60 КБ за такт, строки имён процессов): в управляющем потоке это
        // допустимо, но без нужды его не делаем.
        EnsureNotifier();
        if (!NeedsWorld)
        {
            CheckCables(null);
            Publish();
            return;
        }
        var world = TakeWorld();
        if (RootsKey(world) != _rootsKey) Rebuild(world, "apps started or exited", reopenSessions: true);
        else
        {
            ReconcileBindings(world);
            UpdateDirect(world, reopen: true);
            ReconcileMics(world);
        }
        RunWatchdog(world);
        Publish();
    }

    // ── новые аудиосессии (OnSessionCreated) ──

    /// <summary>
    /// Подписка на новые сессии всех устройств воспроизведения, пока на схеме есть приложения: новое приложение
    /// попадает в «Весь звук системы» сразу при Initialize, а не на следующем такте (этап 0 v2: ~35 мс вместо ~0,5 с).
    /// Такт остаётся запасным путём. Устройства сменились — подписка пересоздаётся.
    /// </summary>
    private void EnsureNotifier()
    {
        if (!NeedsWorld)
        {
            _notifier?.Dispose();
            _notifier = null;
            _notifierKey = "";
            return;
        }
        var render = _devices.Where(d => d.Flow == EDataFlow.Render).OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToList();
        string key = string.Join("|", render.Select(d => d.Id));
        if (_notifier is not null && key == _notifierKey) return;
        _notifier?.Dispose();
        _notifierKey = key;
        _notifier = SessionNotifier.Subscribe(render);
        uint self = (uint)Environment.ProcessId;
        _notifier.Created += (pid, _) =>
        {
            // поток аудиослужбы: только разбудить управляющий поток, всплеск уведомлений — один пересчёт
            if (pid == 0 || pid == self) return;
            if (Interlocked.Exchange(ref _sessionPending, 1) != 0) return;
            Volatile.Write(ref _sessionAt, Stopwatch.GetTimestamp());
            Post(OnSessionCreated);
        };
        if (_notifier.Errors.Count > 0)
            Emit("new-session notifications unavailable on: " + string.Join("; ", _notifier.Errors) + " (polled once a second)");
    }

    private void OnSessionCreated()
    {
        Volatile.Write(ref _sessionPending, 0);
        long at = Volatile.Read(ref _sessionAt);
        if (!NeedsWorld) return;
        var world = TakeWorld();
        if (RootsKey(world) != _rootsKey)
        {
            Rebuild(world, "new audio session", reopenSessions: true);
            Emit($"new audio session handled in {Stopwatch.GetElapsedTime(at).TotalMilliseconds:F0} ms from the notification");
        }
        else
        {
            ReconcileBindings(world);
            UpdateDirect(world, reopen: true);
            ReconcileMics(world);
        }
        Publish();
    }

    // ── мир: процессы и сессии ──

    private AppSnapshot TakeWorld()
    {
        var procs = ProcessSnapshot.Take();
        var render = _devices.Where(d => d.Flow == EDataFlow.Render).Select(d => d.Id);
        return _world = new AppSnapshot(procs, AudioSessions.List(render));
    }

    private IEnumerable<string> AppExes() =>
        _graph.Nodes.Where(n => n.Kind == NodeKind.App && !string.IsNullOrWhiteSpace(n.Exe)).Select(n => ProcessSnapshot.NormalizeExe(n.Exe!)).Distinct();

    /// <summary>Отпечаток корней всех приложений графа: изменился — пересобрать граф.</summary>
    private string RootsKey(AppSnapshot world) =>
        string.Join(";", AppExes().OrderBy(e => e).Select(e => e + ":" + string.Join(",", world.Roots(e))))
        + (UsesVoicemeeter ? "|vm:" + SinkValidator.IsVoicemeeterRunning(world.Processes) : "")
        + SystemKey(world);

    /// <summary>
    /// «Весь звук системы» без приложений со своим узлом захватывает процессы с аудиосессией по одному:
    /// появилась новая сессия или процесс завершился — граф пересобирается (движок трогает только изменившиеся захваты).
    /// </summary>
    private string SystemKey(AppSnapshot world)
    {
        if (_plError is not null || _graph.Nodes.FirstOrDefault(n => n.Kind == NodeKind.System) is not { } sys) return "";
        var exes = GraphBuilder.SystemExclusions(sys, AppExes());
        if (exes.Count == 0) return "";
        var plan = SystemCapture.Plan(world, exes);
        return "|sys:" + string.Join(",", plan.Targets.Select(t => t.Pid)) + "/" + string.Join(",", plan.Skipped.Select(s => $"{s.Exe}:{s.Reason}:{s.Because}"));
    }

    // ── заглушка ──

    private void CheckSink(AppSnapshot world)
    {
        string? id = _graph.SinkEndpoint;
        if (_sink is not null && string.Equals(_sinkCheckedFor, id, StringComparison.OrdinalIgnoreCase)) return;
        _sinkCheckedFor = id;
        if (!_graph.Nodes.Any(GraphBuilder.TakesOver) && id is null)
        {
            _sink = null;
            return;
        }
        if (_probed is { } p && id is not null && string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)
            && _clock.Elapsed.TotalSeconds - p.At < 30)
        {
            _sink = p.Result; // только что проверена из настроек — второй пробный поток не нужен
            _probed = null;
            return;
        }
        _sink = SinkValidator.Check(id, _devices, world.Processes, ep => StreamProbe.TryRender(ep, SinkValidator.ProbeTimeout), MixChannels);
        Emit(_sink.Verdict switch
        {
            SinkVerdict.Ok => $"sink \"{_sink.Endpoint!.Name}\" checked",
            SinkVerdict.Warning => "sink accepted with a warning: " + _sink.Message,
            _ => "sink rejected: " + _sink.Message,
        });
    }

    private string? ActiveSink => _takeOver is not null && _sink is { Usable: true } ? _sink.Endpoint!.Id : null;

    private string? TakeOverUnavailableReason =>
        _opt.DisableTakeOver ? CoreStrings.Bind_DisabledByOption
        : _policy is null ? Loc.F(CoreStrings.Bind_PolicyUnavailableFormat, _policyError)
        : _sink is null ? CoreStrings.Bind_NoSink
        : _sink.Usable ? null : _sink.Message;

    // ── граф ──

    /// <summary>
    /// Пересобрать граф, применить к движку и свести привязки и громкость сессий. Переход на прямой маршрут: сначала
    /// приложение привязывается к выходу, потом гаснет его захват; уход с прямого маршрута — наоборот, сначала
    /// открывается захват, потом приложение уходит в заглушку (этап 0 v2: так разрыв 0–15 мс и 25–45 мс, без двойного звука).
    /// </summary>
    /// <param name="restart">Узлы движка, которые надо пересоздать, даже если они не изменились.</param>
    /// <param name="reopenSessions">Перечитать сессии приложений на прямом маршруте (иначе только громкость).</param>
    private void Rebuild(AppSnapshot world, string reason, Func<string, bool>? restart = null, bool reopenSessions = false)
    {
        CheckSink(world);
        var ctx = new RuntimeContext
        {
            ProcessLoopbackUnavailable = _plError,
            SelfPid = world.Processes.SelfPid,
            Roots = world.Roots,
            OutputRate = OutputRate,
            ActiveSinkEndpointId = ActiveSink,
            VoicemeeterRunning = SinkValidator.IsVoicemeeterRunning(world.Processes),
            PlanSystem = exes => SystemCapture.Plan(world, exes),
            DirectRoutes = _takeOver is not null,
        };
        var build = GraphBuilder.Build(_graph, _devices, _opt.AllowFeedback, ctx);
        if (_takeOver is not null)
        {
            var toDirect = DesiredBindings(build)
                .Where(kv => build.DirectRoutes.Values.Any(r => r.EndpointId == kv.Value && r.Exe == kv.Key)
                             && !string.Equals(_lastDesired.GetValueOrDefault(kv.Key), kv.Value, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            if (toDirect.Count > 0) _takeOver.Reconcile(toDirect, world, partial: true);
        }
        var before = Engine.Sources.Select(s => s.Id).ToHashSet();
        // уход с прямого маршрута: захваты этих узлов открываются сейчас
        var leaving = _takeOver is null ? [] : (_build?.DirectRoutes.Keys ?? []).Where(id => !build.DirectRoutes.ContainsKey(id))
            .SelectMany(id => build.EngineSources.GetValueOrDefault(id, [])).ToHashSet();
        if (leaving.Count > 0)
        {
            // Сначала захват с выключенными проводами: выход их читает, кольца заполняются, но в выход ничего не идёт
            // (приложение пока играет туда само). Когда провода разогрелись, приложение переносится в заглушку и
            // провода включаются в тот же миг — как в замере этапа 0, где захват уже работал к моменту Set.
            var warm = build.Graph with { Wires = build.Graph.Wires.Select(w => leaving.Contains(w.From) ? w with { Enabled = false } : w).ToList() };
            Engine.Apply(warm, restart);
            _build = build;
            long ms = WaitWarm(leaving);
            ReconcileBindings(world);
            Engine.Apply(build.Graph);
            Emit($"direct route left: capture warm after {ms} ms, app moved to the sink and wires turned on");
        }
        else
        {
            Engine.Apply(build.Graph, restart);
        }
        _build = build;
        _rootsKey = RootsKey(world);
        _watchdog.Retain(build.Graph.Sources.Select(s => s.Id));

        var after = build.Graph.Sources.Select(s => s.Id).ToHashSet();
        string Names(IEnumerable<string> ids) => string.Join(", ", ids.Select(DescribeSource));
        var added = after.Except(before).ToList();
        var removed = before.Except(after).ToList();
        if (added.Count > 0 || removed.Count > 0)
            Emit($"{reason}: " + string.Join("; ", new[]
            {
                added.Count > 0 ? "started " + Names(added) : null,
                removed.Count > 0 ? "stopped " + Names(removed) : null,
            }.Where(s => s is not null)));
        ReconcileBindings(world);
        UpdateDirect(world, reopenSessions);
        ReconcileMics(world);
    }

    /// <summary>Сколько ждать разогрева захвата при уходе с прямого маршрута, мс. Приложение, которое молчит, пакетов не даёт — тогда переносится по таймауту.</summary>
    public const int WarmTimeoutMs = 300;

    /// <summary>
    /// Ждать, пока у каждого источника хотя бы один провод заработает (первые пакеты пришли, кольцо заполнено до цели).
    /// Проверка 11 ноября 2026: без этого разрыв при уходе с прямого маршрута был 95 мс — к моменту Set захват только
    /// открывался, и до звука из него были ещё первый пакет, заполнение кольца и очередь выхода.
    /// </summary>
    private long WaitWarm(IReadOnlySet<string> sources)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < WarmTimeoutMs)
        {
            var wires = Engine.Wires;
            if (sources.All(id => wires.Any(w => w.FromId == id && w.State == WireState.Running))) break;
            Thread.Sleep(2);
        }
        return sw.ElapsedMilliseconds;
    }

    /// <summary>Куда привязать каждое приложение в режиме «Забрать себе»: выход прямого маршрута или заглушка (null — недоступна).</summary>
    private Dictionary<string, string?> DesiredBindings(BuildResult? build)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in _graph.Nodes.Where(GraphBuilder.TakesOver))
        {
            string exe = ProcessSnapshot.NormalizeExe(n.Exe!);
            if (build?.DirectRoutes.TryGetValue(n.Id, out var r) == true) d.TryAdd(exe, r.EndpointId);
            else d[exe] = ActiveSink; // два узла одного exe: захват важнее прямого маршрута
        }
        return d;
    }

    private void UpdateDirect(AppSnapshot world, bool reopen)
    {
        if (_direct is null) return;
        var routes = _build?.DirectRoutes ?? new Dictionary<string, DirectRoute>();
        var before = _graph.VolumeApps.ToList();
        _direct.Update(routes, world, reopen, _devices.Where(d => d.Flow == EDataFlow.Render).Select(d => d.Id));
        foreach (string exe in before.Except(_graph.VolumeApps, StringComparer.OrdinalIgnoreCase))
            Emit($"{exe}: session volume restored to 100%");
        foreach (var (exe, error) in _direct.Errors) Emit($"{exe}: {error}");
        if (_direct.Any && _meterDue < Now) _meterDue = Now;
    }

    public string DescribeSource(string engineId)
    {
        string nodeId = GraphBuilder.NodeIdOf(engineId);
        string name = _graph.Nodes.FirstOrDefault(n => n.Id == nodeId)?.Name ?? nodeId;
        return engineId.Length > nodeId.Length ? $"\"{name}\" (PID {engineId[(nodeId.Length + 1)..]})" : $"\"{name}\"";
    }

    /// <summary>Каналы формата микса (GetMixFormat без Initialize); эндпоинты Voicemeeter не опрашиваются.</summary>
    public static int? MixChannels(EndpointInfo ep) => ep.IsVoicemeeter ? null : DeviceCatalog.TryGetMixFormat(ep.Id)?.Channels;

    private int? OutputRate(string endpointId)
    {
        var running = Engine.Outputs.FirstOrDefault(o => string.Equals(o.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase) && o.Format is not null);
        if (running is not null) return running.Format!.SampleRate;
        if (_rateCache.TryGetValue(endpointId, out int? cached)) return cached;
        var ep = _devices.FirstOrDefault(d => d.Id == endpointId);
        // эндпоинты Voicemeeter без нужды не трогаем (этап 0)
        int? rate = ep is null || ep.IsVoicemeeter ? null : DeviceCatalog.TryGetMixFormat(endpointId)?.SampleRate;
        _rateCache[endpointId] = rate;
        return rate;
    }

    // ── горячая замена устройств ──

    /// <summary>Из потока аудиослужбы: только отметить и разбудить управляющий поток.</summary>
    private void OnDeviceChange(DeviceChange c) => Post(() =>
    {
        if (c.Kind == DeviceChangeKind.Format && c.EndpointId is not null) _formatChanged.Add(c.EndpointId);
        _deviceEvents.Hit(Now);
    });

    /// <summary>
    /// Перечитать устройства и пересобрать граф, если что-то изменилось: пропавшее устройство останавливает поток
    /// узла (узел ждёт, провода остаются), вернувшееся запускает его заново, узлы «по умолчанию» переходят на новое
    /// устройство. Сменился формат — узлы на этом устройстве пересоздаются, ресемплеры их проводов пересчитываются.
    /// </summary>
    private void RefreshDevices(string reason)
    {
        var before = _devices;
        var after = DeviceCatalog.ListAll();
        var formats = _formatChanged.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _formatChanged.Clear();
        string? diff = DeviceDiff.Describe(before, after);
        if (diff is null && formats.Count == 0) return;

        var names = after.Concat(before).GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        if (diff is not null) parts.Add(diff);
        parts.AddRange(formats.Select(id => $"format changed on \"{names.GetValueOrDefault(id, id)}\""));
        Emit($"{reason}: {string.Join("; ", parts)}");

        _devices = after;
        _rateCache.Clear();
        var touched = DeviceDiff.Touched(before, after);
        touched.UnionWith(formats);
        if (_graph.SinkEndpoint is { } sink && touched.Contains(sink)) _sink = null; // заглушку проверить заново
        // Сначала пересоздать узлы со сменившимся форматом: частота захвата приложений берётся у работающих выходов,
        // а у нового узла формата ещё нет — частота прочитается у устройства заново.
        if (formats.Count > 0) Engine.Restart(n => formats.Contains(n.EndpointId));
        var world = TakeWorld();
        Rebuild(world, reason, reopenSessions: true);
        EnsureNotifier();
        Publish();
    }

    // ── сон и пробуждение ──

    private void OnPower(bool suspending)
    {
        if (suspending)
        {
            Emit("system is going to sleep");
            return;
        }
        if (Now - _lastResume < 5 || _resume.Pending) return; // PBT_APMRESUMEAUTOMATIC и PBT_APMRESUMESUSPEND приходят парой
        Emit("system woke up");
        _resume.Hit(Now);
    }

    /// <summary>После сна все потоки пересоздаются: клиенты WASAPI могли умереть молча, а часы проводов ушли на время сна.</summary>
    private void Resume()
    {
        _lastResume = Now;
        _sleep.SleptSeconds();
        _formatChanged.Clear();
        _deviceEvents.TryFire(double.MaxValue); // устройства и так перечитываются
        var before = _devices;
        _devices = DeviceCatalog.ListAll();
        _rateCache.Clear();
        if (DeviceDiff.Describe(before, _devices) is { } diff) Emit("after sleep: " + diff);
        var world = TakeWorld();
        _notifier?.Dispose(); // подписки после сна тоже могли умереть
        _notifier = null;
        Rebuild(world, "wake-up", _ => true, reopenSessions: true);
        EnsureNotifier();
        Emit($"wake-up: streams recreated ({Engine.Sources.Count} sources, {Engine.Outputs.Count} outputs)");
        Publish();
    }

    /// <summary>Состояния узлов движка — в журнал (аудиопотоки сами не пишут): кто заработал, кто ждёт устройство, кто в ошибке.</summary>
    private void LogNodeStates()
    {
        var snap = Engine.Snapshot;
        var seen = new HashSet<string>();
        foreach (var n in snap.Sources.Values.Cast<AudioNode>().Concat(snap.Outputs.Values))
        {
            seen.Add(n.Id);
            var st = n.State;
            if (st is NodeState.Starting or NodeState.Stopped) continue;
            if (st == NodeState.Running && n.LowLatencyRequested && n.LowLatency != _lowLatencyStates.GetValueOrDefault(n.Id))
            {
                _lowLatencyStates[n.Id] = n.LowLatency;
                Emit($"{DescribeSource(n.Id)}: " + DescribeLowLatency(n.LowLatency));
            }
            string? msg = st == NodeState.Running ? null : n.Message;
            if (_nodeStates.TryGetValue(n.Id, out var prev) && prev.State == st && prev.Message == msg) continue;
            _nodeStates[n.Id] = (st, msg);
            Emit($"{DescribeSource(n.Id)}: " + st switch
            {
                NodeState.Running => "running" + (n.Format is { } f ? $" ({f.SampleRate} Hz, {f.Channels} ch)" : ""),
                NodeState.WaitingDevice => "waiting: " + msg,
                _ => "error: " + msg,
            });
        }
        foreach (string id in _nodeStates.Keys.Where(id => !seen.Contains(id)).ToList()) _nodeStates.Remove(id);
        foreach (string id in _lowLatencyStates.Keys.Where(id => !seen.Contains(id)).ToList()) _lowLatencyStates.Remove(id);
    }

    /// <summary>Строка журнала о режиме «Низкая задержка» (журнал на английском).</summary>
    public static string DescribeLowLatency(LowLatencyInfo i) => i.Status switch
    {
        LowLatencyStatus.Active => FormattableString.Invariant($"low latency on, period {i.PeriodFrames} frames ({i.PeriodMs:0.##} ms, default {i.DefaultFrames})"),
        LowLatencyStatus.Unsupported => FormattableString.Invariant($"low latency not supported by the driver: minimum period {i.MinFrames} frames ({i.MinMs:0.##} ms)"),
        LowLatencyStatus.Unavailable => $"low latency unavailable: {i.Error}; usual period",
        LowLatencyStatus.FellBack => FormattableString.Invariant($"low latency off after more than {GlitchWindow.Limit} glitches in a minute; back to {i.DefaultFrames} frames ({i.DefaultMs:0.##} ms)"),
        _ => "low latency off",
    };

    // ── «Забрать себе» ──

    private Dictionary<string, AppBinding> _lastBindings = new();

    private void ReconcileBindings(AppSnapshot world)
    {
        if (_takeOver is null) return;
        var desired = DesiredBindings(_build);
        var journalBefore = _graph.BoundApps.ToList();
        _takeOver.Reconcile(desired, world, TakeOverUnavailableReason);
        _lastDesired = desired;
        foreach (var (exe, b) in _takeOver.Status)
        {
            if (_lastBindings.TryGetValue(exe, out var prev) && prev.State == b.State && prev.PlaysDirect == b.PlaysDirect
                && string.Equals(prev.Target, b.Target, StringComparison.OrdinalIgnoreCase)) continue;
            Emit($"{exe}: {DescribeForLog(b)}");
        }
        foreach (string exe in journalBefore.Except(_graph.BoundApps, StringComparer.OrdinalIgnoreCase))
            Emit($"{exe}: redirect removed, the app plays to its own device again");
        _lastBindings = new Dictionary<string, AppBinding>(_takeOver.Status, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Для журнала: прямой маршрут называет выход.</summary>
    private string DescribeForLog(AppBinding b) =>
        b.State == BindingState.Bound && b.Target is { } t && !string.Equals(t, ActiveSink, StringComparison.OrdinalIgnoreCase)
            ? $"direct route to \"{_devices.FirstOrDefault(d => string.Equals(d.Id, t, StringComparison.OrdinalIgnoreCase))?.Name ?? t}\""
              + (b.PlaysDirect ? ", but the app still plays to its old device: restart playback" : "")
            : Describe(b);

    public static string Describe(AppBinding b) => b.State switch
    {
        BindingState.Bound => b.Message is null ? CoreStrings.Bind_StateBound : Loc.F(CoreStrings.Bind_StateBoundFormat, b.Message),
        BindingState.NotRunning => CoreStrings.Bind_StateNotRunning,
        BindingState.WaitingAudio => CoreStrings.Bind_StateWaitingAudio,
        BindingState.ResetPending => b.Message ?? CoreStrings.Bind_StateResetPending,
        BindingState.Unavailable => Loc.F(CoreStrings.Bind_StateUnavailableFormat, b.Message),
        _ => Loc.F(CoreStrings.Bind_StateErrorFormat, b.Message),
    };

    // ── кабели: назначенный микрофон и проверки ──

    /// <summary>
    /// Назначить приложениям из micFor выход кабеля устройством записи и снять назначение у тех, кого убрали из списков
    /// (журнал micApps). При выходе из микшера назначения остаются: приложение и дальше читает кабель, как после Earmark.
    /// </summary>
    private void ReconcileMics(AppSnapshot world)
    {
        if (_mic is not null)
        {
            var desired = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in _graph.Nodes)
            {
                if (n.MicFor is not { Count: > 0 } apps || n.Kind != NodeKind.DeviceOutput) continue;
                string? target = CableTable.Default.PairOf(n, _devices)?.Output?.Id;
                foreach (string app in apps) desired.TryAdd(ProcessSnapshot.NormalizeExe(app), target);
            }
            var journalBefore = _graph.MicApps.ToList();
            _mic.Reconcile(desired, world, CoreStrings.Mic_CableOutputMissing);
            foreach (var (exe, b) in _mic.Status)
            {
                if (_lastMic.TryGetValue(exe, out var prev) && prev.State == b.State && string.Equals(prev.Target, b.Target, StringComparison.OrdinalIgnoreCase)) continue;
                string what = b.State == BindingState.Bound
                    ? $"set to \"{_devices.FirstOrDefault(d => string.Equals(d.Id, b.Target, StringComparison.OrdinalIgnoreCase))?.Name ?? b.Target}\""
                    : Describe(b);
                Emit($"{exe}: microphone {what}");
            }
            foreach (string exe in journalBefore.Except(_graph.MicApps, StringComparer.OrdinalIgnoreCase))
                Emit($"{exe}: microphone assignment removed, the app records from its own device again");
            _lastMic = new Dictionary<string, AppBinding>(_mic.Status, StringComparer.OrdinalIgnoreCase);
        }
        CheckCables(world);
    }

    /// <summary>Проверки кабелей: кто ещё пишет в кабель-микрофон, частоты концов, кого читает приложение с назначенным кабелем.</summary>
    private void CheckCables(AppSnapshot? world)
    {
        bool assigned = world is not null && _graph.Nodes.Any(n => n.MicFor is { Count: > 0 });
        _captureSessions = assigned ? AudioSessions.List(_devices.Where(d => d.Flow == EDataFlow.Capture && !d.IsVoicemeeter).Select(d => d.Id)) : null;
        _cableWarnings = CableChecks.Check(_graph, _devices, CableTable.Default, world, _captureSessions, RateOf)
            .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }

    /// <summary>Частота микса устройства (кэш общий с выходами); эндпоинты Voicemeeter не опрашиваются.</summary>
    private int? RateOf(string endpointId)
    {
        if (_rateCache.TryGetValue(endpointId, out int? cached)) return cached;
        var ep = _devices.FirstOrDefault(d => d.Id == endpointId);
        int? rate = ep is null || ep.IsVoicemeeter ? null : DeviceCatalog.TryGetMixFormat(endpointId)?.SampleRate;
        _rateCache[endpointId] = rate;
        return rate;
    }

    // ── сторожевой таймер ──

    private void RunWatchdog(AppSnapshot world)
    {
        double now = _clock.Elapsed.TotalSeconds;
        foreach (var s in Engine.Sources)
        {
            if (!s.IsProcessLoopback) continue;
            bool active = s.Kind == SourceKind.System ? world.OthersHaveActiveSession() : world.TreeHasActiveSession(s.ProcessId);
            bool open = s.State is NodeState.Running or NodeState.Starting;
            if (!_watchdog.Check(s.Id, now, active, open, s.SecondsSinceActivity)) continue;
            int n = _restarts[s.Id] = _restarts.GetValueOrDefault(s.Id) + 1;
            Emit($"watchdog: {DescribeSource(s.Id)} has had no audio for {s.SecondsSinceActivity:F0} s with an active session, recreating the capture (attempt {n})");
            Engine.RestartSource(s.Id);
        }
    }

    private void Publish()
    {
        _status = new HostStatus
        {
            Build = _build,
            ProcessLoopbackError = _plError,
            PolicyError = _policyError,
            Sink = _sink,
            Bindings = _takeOver is null ? new Dictionary<string, AppBinding>() : new Dictionary<string, AppBinding>(_takeOver.Status, StringComparer.OrdinalIgnoreCase),
            WatchdogRestarts = new Dictionary<string, int>(_restarts),
            BoundApps = _graph.BoundApps.ToList(),
            Devices = _devices,
            Graph = _graph,
            DeviceWatchError = _watcher is null ? _watchError : null,
            DirectMeters = _direct?.Meters ?? new Dictionary<string, Mixer.Audio.Dsp.PeakMeter>(),
            DirectSessions = _direct?.SessionCounts ?? new Dictionary<string, int>(),
            DirectErrors = _direct is null ? new Dictionary<string, string>() : new Dictionary<string, string>(_direct.Errors, StringComparer.OrdinalIgnoreCase),
            VolumeApps = _graph.VolumeApps.ToList(),
            PlayingApps = PlayingApps(_world),
            MicBindings = _mic is null ? new Dictionary<string, AppBinding>() : new Dictionary<string, AppBinding>(_mic.Status, StringComparer.OrdinalIgnoreCase),
            MicApps = _graph.MicApps.ToList(),
            CableWarnings = _cableWarnings,
        };
    }

    private static List<string> PlayingApps(AppSnapshot? world)
    {
        if (world is null) return [];
        var self = world.Processes.Tree([world.Processes.SelfPid]);
        return world.Sessions
            .Where(s => s.Pid != 0 && s.State != AudioSessionState.Expired && !self.Contains(s.Pid) && world.Processes.ByPid.ContainsKey(s.Pid))
            .Select(s => ProcessSnapshot.NormalizeExe(world.Processes.ExeOf(s.Pid)))
            .Distinct().Order(StringComparer.Ordinal).ToList();
    }

    // ── журнал boundApps в конфиге ──

    private sealed class Journal(MixerHost host) : IBindingJournal
    {
        public IReadOnlyCollection<string> Apps => host._graph.BoundApps;

        public void Add(string exe) => Commit(host._graph.BoundApps.Append(exe).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

        public void Remove(string exe) => Commit(host._graph.BoundApps.Where(a => !a.Equals(exe, StringComparison.OrdinalIgnoreCase)).ToList());

        private void Commit(List<string> apps)
        {
            var g = host._graph with { BoundApps = apps };
            host._save?.Invoke(g); // сначала на диск: при ошибке привязка не ставится
            Volatile.Write(ref host._graph, g);
        }
    }

    /// <summary>Журнал volumeApps в конфиге: приложения, которым микшер уменьшил громкость сессии.</summary>
    private sealed class VolumeJournal(MixerHost host) : IBindingJournal
    {
        public IReadOnlyCollection<string> Apps => host._graph.VolumeApps;

        public void Add(string exe) => Commit(host._graph.VolumeApps.Append(exe).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

        public void Remove(string exe) => Commit(host._graph.VolumeApps.Where(a => !a.Equals(exe, StringComparison.OrdinalIgnoreCase)).ToList());

        private void Commit(List<string> apps)
        {
            var g = host._graph with { VolumeApps = apps };
            host._save?.Invoke(g); // сначала на диск: при ошибке громкость не меняется
            Volatile.Write(ref host._graph, g);
        }
    }

    /// <summary>Журнал micApps в конфиге: приложения, которым микшер назначил кабель микрофоном.</summary>
    private sealed class MicJournal(MixerHost host) : IBindingJournal
    {
        public IReadOnlyCollection<string> Apps => host._graph.MicApps;

        public void Add(string exe) => Commit(host._graph.MicApps.Append(exe).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

        public void Remove(string exe) => Commit(host._graph.MicApps.Where(a => !a.Equals(exe, StringComparison.OrdinalIgnoreCase)).ToList());

        private void Commit(List<string> apps)
        {
            var g = host._graph with { MicApps = apps };
            host._save?.Invoke(g); // сначала на диск: при ошибке назначение не ставится
            Volatile.Write(ref host._graph, g);
        }
    }

    /// <summary>
    /// Штатный выход: остановить звук, затем снять привязки у живых приложений (сначала тишина, а не двойной звук),
    /// вернуть громкость сессий прямых маршрутов, сохранить журналы. Приложения, которые сейчас не запущены,
    /// остаются в журналах до следующего запуска.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        var done = new ManualResetEventSlim(false);
        if (_thread is not null)
        {
            _queue.Add(() =>
            {
                try
                {
                    _watcher?.Dispose(); // до остановки движка: новые события больше не нужны
                    _power?.Dispose();
                    _notifier?.Dispose();
                    Engine.Dispose();
                    if (_takeOver is not null)
                    {
                        var world = TakeWorld();
                        _takeOver.ReleaseAll(world);
                        foreach (var (exe, b) in _takeOver.Status) Emit($"{exe}: {Describe(b)}");
                        var left = _graph.BoundApps;
                        Emit(left.Count == 0 ? "all redirects removed" : $"left in the journal: {string.Join(", ", left)}; they will be removed on the next start");
                        // привязки сняты — теперь вернуть громкость: приложение не прозвучит на миг громко в прежнем выходе
                        _direct?.RestoreAll(world, _devices.Where(d => d.Flow == EDataFlow.Render).Select(d => d.Id));
                        if (_graph.VolumeApps.Count > 0)
                            Emit($"session volume left in the journal: {string.Join(", ", _graph.VolumeApps)}; restored on the next start");
                        _direct?.Dispose();
                    }
                }
                finally
                {
                    _policy?.Dispose();
                    done.Set();
                }
            });
            _disposed = true;
            if (!done.Wait(TimeSpan.FromSeconds(15))) Emit("exit: the control thread didn't respond within 15 s");
            _thread.Join(2000);
        }
        else
        {
            _disposed = true;
            Engine.Dispose();
        }
    }
}
