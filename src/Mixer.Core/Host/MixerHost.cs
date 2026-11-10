using System.Collections.Concurrent;
using System.Diagnostics;
using Mixer.Audio.Engine;
using Mixer.Audio.Interop;
using Mixer.Core.Apps;
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
        Volatile.Write(ref _graph, next with { BoundApps = prev.BoundApps });
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
        Rebuild(world, "graph changed");
        // «Забрать себе» включили или выключили — привязка меняется сразу, а не на следующем такте
        if (change == GraphChangeKind.Structure) ReconcileBindings(world);
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
            if (_policy is not null) _takeOver = new TakeOverManager(_policy, new Journal(this));
            else
                Emit("Take over unavailable: " + _policyError + ". Fallback: pick the app's device in the Windows volume mixer");
        }

        var world = TakeWorld();
        if (_graph.BoundApps.Count > 0)
            Emit($"redirect journal from the last run: {string.Join(", ", _graph.BoundApps)}");
        CheckSink(world);
        Rebuild(world, "start");
        ReconcileBindings(world);
        Publish();
    }

    /// <summary>Процессы и сессии нужны, только если в графе есть приложения или в журнале остались привязки.</summary>
    private bool NeedsWorld =>
        _graph.BoundApps.Count > 0 || _graph.Nodes.Any(n => n.Kind is NodeKind.App or NodeKind.System) || UsesVoicemeeter;

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
        if (!NeedsWorld)
        {
            Publish();
            return;
        }
        var world = TakeWorld();
        if (RootsKey(world) != _rootsKey) Rebuild(world, "apps started or exited");
        ReconcileBindings(world);
        RunWatchdog(world);
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
        if (_plError is not null || !_graph.Nodes.Any(n => n.Kind == NodeKind.System)) return "";
        var exes = AppExes().ToList();
        if (exes.Count == 0) return "";
        var plan = SystemCapture.Plan(world, exes);
        return "|sys:" + string.Join(",", plan.Targets.Select(t => t.Pid)) + "/" + string.Join(",", plan.Skipped);
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

    /// <param name="restart">Узлы движка, которые надо пересоздать, даже если они не изменились.</param>
    private void Rebuild(AppSnapshot world, string reason, Func<string, bool>? restart = null)
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
        };
        var build = GraphBuilder.Build(_graph, _devices, _opt.AllowFeedback, ctx);
        var before = Engine.Sources.Select(s => s.Id).ToHashSet();
        Engine.Apply(build.Graph, restart);
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
        Rebuild(world, reason);
        ReconcileBindings(world);
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
        Rebuild(world, "wake-up", _ => true);
        Emit($"wake-up: streams recreated ({Engine.Sources.Count} sources, {Engine.Outputs.Count} outputs)");
        ReconcileBindings(world);
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
    }

    // ── «Забрать себе» ──

    private Dictionary<string, AppBinding> _lastBindings = new();

    private void ReconcileBindings(AppSnapshot world)
    {
        if (_takeOver is null) return;
        var desired = _graph.Nodes.Where(GraphBuilder.TakesOver).Select(n => n.Exe!);
        var journalBefore = _graph.BoundApps.ToList();
        _takeOver.Reconcile(desired, ActiveSink, world, TakeOverUnavailableReason);
        foreach (var (exe, b) in _takeOver.Status)
        {
            if (_lastBindings.TryGetValue(exe, out var prev) && prev.State == b.State && prev.PlaysDirect == b.PlaysDirect) continue;
            Emit($"{exe}: {Describe(b)}");
        }
        foreach (string exe in journalBefore.Except(_graph.BoundApps, StringComparer.OrdinalIgnoreCase))
            Emit($"{exe}: redirect removed, the app plays to its own device again");
        _lastBindings = new Dictionary<string, AppBinding>(_takeOver.Status, StringComparer.OrdinalIgnoreCase);
    }

    public static string Describe(AppBinding b) => b.State switch
    {
        BindingState.Bound => b.Message is null ? CoreStrings.Bind_StateBound : Loc.F(CoreStrings.Bind_StateBoundFormat, b.Message),
        BindingState.NotRunning => CoreStrings.Bind_StateNotRunning,
        BindingState.WaitingAudio => CoreStrings.Bind_StateWaitingAudio,
        BindingState.ResetPending => b.Message ?? CoreStrings.Bind_StateResetPending,
        BindingState.Unavailable => Loc.F(CoreStrings.Bind_StateUnavailableFormat, b.Message),
        _ => Loc.F(CoreStrings.Bind_StateErrorFormat, b.Message),
    };

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
        };
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

    /// <summary>
    /// Штатный выход: остановить звук, затем снять привязки у живых приложений (сначала тишина, а не двойной звук),
    /// сохранить журнал. Приложения, которые сейчас не запущены, остаются в журнале до следующего запуска.
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
                    Engine.Dispose();
                    if (_takeOver is not null)
                    {
                        var world = TakeWorld();
                        _takeOver.ReleaseAll(world);
                        foreach (var (exe, b) in _takeOver.Status) Emit($"{exe}: {Describe(b)}");
                        var left = _graph.BoundApps;
                        Emit(left.Count == 0 ? "all redirects removed" : $"left in the journal: {string.Join(", ", left)}; they will be removed on the next start");
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
