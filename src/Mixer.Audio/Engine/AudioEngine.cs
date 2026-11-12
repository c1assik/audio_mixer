using Mixer.Audio.Dsp;
using System.Runtime;

namespace Mixer.Audio.Engine;

/// <summary>
/// Источник в снимке маршрутизации. Для process loopback (App, System) EndpointId пуст, ProcessId — цель
/// (или исключаемый процесс), SampleRate — частота захвата (её конвертирует Windows).
/// </summary>
public sealed record SourceSpec(string Id, SourceKind Kind, string EndpointId, float GainDb = 0, bool Muted = false, bool Mono = false, float Balance = 0,
    uint ProcessId = 0, int SampleRate = 0)
{
    /// <summary>Обработка перед фейдером (null — нет).</summary>
    public ChainSettings? Chain { get; init; }
    /// <summary>«Низкая задержка» (IAudioClient3): только у входа устройства.</summary>
    public bool LowLatency { get; init; }
    /// <summary>Слоты плагинов цепочки (их создаёт и ведёт управляющий слой).</summary>
    public IReadOnlyList<PluginSlot> Plugins { get; init; } = [];
}

/// <summary>Выход в снимке маршрутизации.</summary>
public sealed record OutputSpec(string Id, string EndpointId, float GainDb = 0, bool Muted = false)
{
    public ChainSettings? Chain { get; init; }
    /// <summary>«Низкая задержка» (IAudioClient3).</summary>
    public bool LowLatency { get; init; }
    public IReadOnlyList<PluginSlot> Plugins { get; init; } = [];
}

/// <summary>Шина: своих часов нет, считается в периоде каждого выхода, в который ведёт.</summary>
public sealed record BusSpec(string Id, float GainDb = 0, bool Muted = false, float Balance = 0)
{
    public ChainSettings? Chain { get; init; }
    public IReadOnlyList<PluginSlot> Plugins { get; init; } = [];
    /// <summary>Главный выход закреплённой шины, выбранный пользователем (null — выбирает движок).</summary>
    public string? ClockOutput { get; init; }
    /// <summary>С плагинами шина считается один раз, в потоке главного выхода (<see cref="MixPlanner"/>).</summary>
    public bool Pinned => Plugins.Count > 0;
}

/// <summary>Провод в снимке маршрутизации: источник или шина → шина или выход.</summary>
public sealed record WireSpec(string From, string To, float GainDb = 0, bool Enabled = true);

/// <summary>Готовый снимок маршрутизации: движок не знает ни о графе, ни об интерфейсе.</summary>
public sealed record EngineGraph(IReadOnlyList<SourceSpec> Sources, IReadOnlyList<OutputSpec> Outputs, IReadOnlyList<WireSpec> Wires)
{
    public static readonly EngineGraph Empty = new([], [], []);

    public IReadOnlyList<BusSpec> Buses { get; init; } = [];
}

/// <summary>
/// Узлы движка на момент последнего Apply. Читается без блокировки из любого потока (интерфейс забирает пики
/// 30 раз в секунду и не должен ждать Apply, который может стоять до 2 с на остановке зависшего узла).
/// Провод в шину, которая идёт в несколько выходов, есть в нескольких экземплярах (по одному на выход, Out).
/// </summary>
public sealed record EngineSnapshot(
    IReadOnlyDictionary<string, SourceNode> Sources,
    IReadOnlyDictionary<string, OutputNode> Outputs,
    IReadOnlyDictionary<(string From, string To, string Out), Wire> Wires)
{
    public static readonly EngineSnapshot Empty = new(
        new Dictionary<string, SourceNode>(), new Dictionary<string, OutputNode>(), new Dictionary<(string, string, string), Wire>());

    /// <summary>Экземпляры шин: по одному на выход, в который шина ведёт.</summary>
    public IReadOnlyDictionary<(string Bus, string Out), BusInstance> Buses { get; init; } = new Dictionary<(string, string), BusInstance>();
    /// <summary>Провода из шин (по экземпляру на выход).</summary>
    public IReadOnlyDictionary<(string From, string To, string Out), BusLink> Links { get; init; } = new Dictionary<(string, string, string), BusLink>();
}

/// <summary>
/// Аудиодвижок. Apply получает новый снимок, создаёт и останавливает узлы, затем каждому узлу
/// атомарно подменяет неизменяемый массив его проводов. Аудиопотоки читают ссылку в начале блока.
/// Apply и Dispose вызываются из одного управляющего потока (не из потока интерфейса: остановка узла может ждать WASAPI).
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SourceNode> _sources = new();
    private readonly Dictionary<string, OutputNode> _outputs = new();
    private readonly Dictionary<(string From, string To, string Out), Wire> _wires = new();
    private readonly Dictionary<(string Bus, string Out), BusInstance> _buses = new();
    private readonly Dictionary<(string From, string To, string Out), BusLink> _links = new();
    private EngineGraph _last = EngineGraph.Empty;
    private volatile EngineSnapshot _snapshot = EngineSnapshot.Empty;
    /// <summary>
    /// Откаты с малого периода по узлу и устройству. Пересозданный узел (сон, переподключение, перестройка) сразу
    /// идёт с обычным периодом; память стирается, когда «Низкую задержку» у узла выключают.
    /// </summary>
    private readonly Dictionary<(string Id, string Endpoint), LowLatencyInfo> _fallbacks = new();

    /// <summary>Сколько ждать выхода потока узла при остановке, мс; дольше — поток бросается.</summary>
    public const int StopTimeoutMs = 2000;
    /// <summary>То же при принудительном перезапуске (сторожевой таймер, выход из сна): зависший поток не должен держать управление.</summary>
    public const int RestartStopTimeoutMs = 500;

    public EngineOptions Options { get; }

    public AudioEngine(EngineOptions? options = null)
    {
        Options = options ?? new EngineOptions();
        // Паузы GC — главный источник щелчков в .NET: просим сборщик не делать блокирующих сборок.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        Dsp.SincTable.Prewarm([44100, 48000]);
    }

    /// <summary>Узлы без блокировки (см. <see cref="EngineSnapshot"/>).</summary>
    public EngineSnapshot Snapshot => _snapshot;

    public IReadOnlyCollection<SourceNode> Sources { get { lock (_gate) return _sources.Values.ToList(); } }
    public IReadOnlyCollection<OutputNode> Outputs { get { lock (_gate) return _outputs.Values.ToList(); } }
    public IReadOnlyCollection<Wire> Wires { get { lock (_gate) return _wires.Values.ToList(); } }

    public SourceNode? FindSource(string id) { lock (_gate) return _sources.GetValueOrDefault(id); }
    public OutputNode? FindOutput(string id) { lock (_gate) return _outputs.GetValueOrDefault(id); }
    /// <summary>Первый экземпляр провода from → to (у провода в шину их может быть несколько, по выходам).</summary>
    public Wire? FindWire(string from, string to) { lock (_gate) return _wires.Values.FirstOrDefault(w => w.FromId == from && w.ToId == to); }

    /// <summary>
    /// Пересоздать источник (сторожевой таймер: захват при активной сессии перестал получать пакеты).
    /// Новый узел получает новые провода, старый поток останавливается с коротким таймаутом и при зависании бросается.
    /// </summary>
    public bool RestartSource(string id)
    {
        lock (_gate)
        {
            if (!_sources.ContainsKey(id)) return false;
            Apply(_last, n => n == id);
            return true;
        }
    }

    /// <summary>
    /// Пересоздать узлы движка, подходящие под условие: выход из сна, смена формата устройства.
    /// Возвращает, сколько узлов пересоздано.
    /// </summary>
    public int Restart(Func<AudioNode, bool> which)
    {
        lock (_gate)
        {
            var ids = _sources.Values.Cast<AudioNode>().Concat(_outputs.Values).Where(which).Select(n => n.Id).ToHashSet();
            if (ids.Count == 0) return 0;
            Apply(_last, ids.Contains);
            return ids.Count;
        }
    }

    public void Apply(EngineGraph graph) => Apply(graph, null);

    /// <param name="restart">Узлы, которые надо пересоздать, даже если они не изменились (null — никакие).</param>
    public void Apply(EngineGraph graph, Func<string, bool>? restart)
    {
        lock (_gate)
        {
            _last = graph;
            var toStop = new List<AudioNode>();
            var toStart = new List<AudioNode>();
            RememberFallbacks(graph);

            // ── источники ──
            var newSources = new Dictionary<string, SourceNode>();
            foreach (var s in graph.Sources)
            {
                bool lowLatency = s.LowLatency && s.Kind == SourceKind.DeviceInput;
                if (_sources.TryGetValue(s.Id, out var cur) && cur.Kind == s.Kind && cur.EndpointId == s.EndpointId
                    && cur.ProcessId == s.ProcessId && cur.SampleRate == s.SampleRate && cur.LowLatencyRequested == lowLatency
                    && restart?.Invoke(s.Id) != true)
                {
                    newSources[s.Id] = cur;
                }
                else
                {
                    if (cur is not null) toStop.Add(cur);
                    cur = new SourceNode(s.Id, s.Kind, s.EndpointId, Options, s.ProcessId, s.SampleRate,
                        lowLatency, _fallbacks.GetValueOrDefault((s.Id, s.EndpointId)));
                    newSources[s.Id] = cur;
                    toStart.Add(cur);
                }
                cur.SetGain(s.GainDb, s.Balance, s.Muted);
                cur.Mono = s.Mono;
                cur.Chain.Set(s.Chain);
                cur.Chain.SetPlugins(s.Plugins);
            }
            foreach (var (id, node) in _sources)
                if (!newSources.ContainsKey(id)) toStop.Add(node);

            // ── выходы ──
            var newOutputs = new Dictionary<string, OutputNode>();
            foreach (var o in graph.Outputs)
            {
                if (_outputs.TryGetValue(o.Id, out var cur) && cur.EndpointId == o.EndpointId && cur.LowLatencyRequested == o.LowLatency
                    && restart?.Invoke(o.Id) != true)
                {
                    newOutputs[o.Id] = cur;
                }
                else
                {
                    if (cur is not null) toStop.Add(cur);
                    cur = new OutputNode(o.Id, o.EndpointId, Options, o.LowLatency, _fallbacks.GetValueOrDefault((o.Id, o.EndpointId)));
                    newOutputs[o.Id] = cur;
                    toStart.Add(cur);
                }
                cur.SetGain(o.GainDb, 0, o.Muted);
                cur.Chain.Set(o.Chain);
                cur.Chain.SetPlugins(o.Plugins);
            }
            foreach (var (id, node) in _outputs)
                if (!newOutputs.ContainsKey(id)) toStop.Add(node);

            // ── шины и провода: раскладка по выходам ──
            var busSpecs = new Dictionary<string, BusSpec>();
            foreach (var b in graph.Buses) busSpecs.TryAdd(b.Id, b);
            var live = graph with { Sources = graph.Sources.Where(x => newSources.ContainsKey(x.Id)).ToList() };
            var newWires = new Dictionary<(string, string, string), Wire>();
            var newBuses = new Dictionary<(string, string), BusInstance>();
            var newLinks = new Dictionary<(string, string, string), BusLink>();
            var plans = new Dictionary<string, MixPlan>();
            var routes = MixPlanner.Plan(live, out var clocks).Where(r => newOutputs.ContainsKey(r.OutputId)).ToList();
            // выход пересоздан — его шины и провода тоже новые (старые экземпляры знал только старый поток)
            bool SameOutput(string o) => _outputs.GetValueOrDefault(o) == newOutputs[o];

            // сначала все экземпляры шин: провод из закреплённой шины берёт экземпляр из маршрута её главного выхода
            foreach (var route in routes)
            {
                string o = route.OutputId;
                foreach (string id in route.Buses)
                {
                    if (!SameOutput(o) || !_buses.TryGetValue((id, o), out var bus)) bus = new BusInstance(id, o, Options.MaxBlockFrames, Options.PeriodMs);
                    var spec = busSpecs[id];
                    bus.SetGain(spec.GainDb, spec.Balance, spec.Muted);
                    bus.Chain.Set(spec.Chain);
                    bus.Chain.SetPlugins(spec.Plugins);
                    newBuses[(id, o)] = bus;
                }
            }

            var taps = new Dictionary<BusInstance, List<Wire>>();
            foreach (var route in routes)
            {
                string o = route.OutputId;
                bool sameOutput = SameOutput(o);

                Wire WireFor(WireSpec w)
                {
                    var src = newSources[w.From];
                    var key = (w.From, w.To, o);
                    Wire? wire = null;
                    bool same = sameOutput && _wires.TryGetValue(key, out wire) && _sources.GetValueOrDefault(w.From) == src;
                    if (!same) wire = new Wire(w.From, w.To, src.Clock, Options, o);
                    wire!.GainDb = w.GainDb;
                    wire.Enabled = w.Enabled;
                    newWires[key] = wire;
                    return wire;
                }

                // провод из закреплённой шины другого выхода: кольцо, пишет поток того выхода
                Wire RemoteFor(WireSpec w)
                {
                    var from = newBuses[(w.From, clocks[w.From])];
                    var key = (w.From, w.To, o);
                    Wire? wire = null;
                    bool same = sameOutput && _wires.TryGetValue(key, out wire) && wire.Clock == from.Clock;
                    if (!same) wire = new Wire(w.From, w.To, from.Clock, Options, o);
                    wire!.GainDb = w.GainDb;
                    wire.Enabled = w.Enabled;
                    newWires[key] = wire;
                    if (!taps.TryGetValue(from, out var list)) taps[from] = list = [];
                    list.Add(wire);
                    return wire;
                }

                BusLink LinkFor(WireSpec w)
                {
                    var key = (w.From, w.To, o);
                    var from = newBuses[(w.From, o)];
                    if (!_links.TryGetValue(key, out var link) || link.From != from) link = new BusLink(from, w.To);
                    link.GainDb = w.GainDb;
                    link.Enabled = w.Enabled;
                    newLinks[key] = link;
                    return link;
                }

                Wire[] WiresInto(string to) =>
                    route.SourceWires.Where(w => w.To == to).Select(WireFor)
                        .Concat(route.RemoteWires.Where(w => w.To == to && newBuses.ContainsKey((w.From, clocks[w.From]))).Select(RemoteFor)).ToArray();

                var steps = route.Buses.Select(id => new MixStep
                {
                    Bus = newBuses[(id, o)],
                    Wires = WiresInto(id),
                    Links = route.BusWires.Where(w => w.To == id).Select(LinkFor).ToArray(),
                }).ToArray();
                plans[o] = new MixPlan
                {
                    Steps = steps,
                    Wires = WiresInto(o),
                    Links = route.BusWires.Where(w => w.To == o).Select(LinkFor).ToArray(),
                };
            }
            foreach (var bus in newBuses.Values) bus.PublishTaps(taps.TryGetValue(bus, out var t) ? [.. t] : []);

            // Удаляемые провода сначала гасим фейдом mute (5 мс), чтобы не было щелчка.
            bool faded = false;
            foreach (var (key, wire) in _wires)
            {
                if (newWires.ContainsKey(key) || !wire.Enabled) continue;
                wire.Enabled = false;
                faded = true;
            }
            foreach (var (key, link) in _links)
            {
                if (newLinks.ContainsKey(key) || !link.Enabled) continue;
                link.Enabled = false;
                faded = true;
            }
            if (faded) Thread.Sleep(30);

            // ── публикация ──
            foreach (var src in newSources.Values)
                src.PublishWires(newWires.Values.Where(w => newSources.GetValueOrDefault(w.FromId) == src).ToArray());
            foreach (var (id, dst) in newOutputs)
                dst.PublishPlan(plans.GetValueOrDefault(id) ?? MixPlan.Empty);

            _sources.Clear();
            foreach (var kv in newSources) _sources[kv.Key] = kv.Value;
            _outputs.Clear();
            foreach (var kv in newOutputs) _outputs[kv.Key] = kv.Value;
            _wires.Clear();
            foreach (var kv in newWires) _wires[kv.Key] = kv.Value;
            _buses.Clear();
            foreach (var kv in newBuses) _buses[kv.Key] = kv.Value;
            _links.Clear();
            foreach (var kv in newLinks) _links[kv.Key] = kv.Value;
            _snapshot = new EngineSnapshot(
                new Dictionary<string, SourceNode>(newSources), new Dictionary<string, OutputNode>(newOutputs),
                new Dictionary<(string, string, string), Wire>(newWires))
            {
                Buses = new Dictionary<(string, string), BusInstance>(newBuses),
                Links = new Dictionary<(string, string, string), BusLink>(newLinks),
            };

            StopAll(toStop, restart is not null ? RestartStopTimeoutMs : StopTimeoutMs);
            foreach (var n in toStart) n.Start();
        }
    }

    /// <summary>
    /// Запомнить откаты живых узлов (до их пересоздания) и забыть откаты узлов, у которых «Низкую задержку» выключили:
    /// выключить и включить режим — способ попробовать снова.
    /// </summary>
    private void RememberFallbacks(EngineGraph graph)
    {
        foreach (var n in _sources.Values.Cast<AudioNode>().Concat(_outputs.Values))
            if (n.FellBack is { } fb) _fallbacks[(n.Id, n.EndpointId)] = fb;
        if (_fallbacks.Count == 0) return;
        var on = graph.Sources.Where(s => s.LowLatency).Select(s => s.Id)
            .Concat(graph.Outputs.Where(o => o.LowLatency).Select(o => o.Id)).ToHashSet();
        var present = graph.Sources.Select(s => s.Id).Concat(graph.Outputs.Select(o => o.Id)).ToHashSet();
        // узел, которого сейчас нет в движке (устройство отключено), помнит откат до возвращения
        foreach (var key in _fallbacks.Keys.Where(k => present.Contains(k.Id) && !on.Contains(k.Id)).ToList())
            _fallbacks.Remove(key);
    }

    /// <summary>Откаты с малого периода, которые движок помнит (для журнала и тестов).</summary>
    public IReadOnlyDictionary<(string Id, string Endpoint), LowLatencyInfo> Fallbacks
    {
        get { lock (_gate) return new Dictionary<(string, string), LowLatencyInfo>(_fallbacks); }
    }

    /// <summary>Сначала просим выйти все потоки, потом ждём: несколько зависших узлов не складывают таймауты.</summary>
    private static void StopAll(IReadOnlyList<AudioNode> nodes, int timeoutMs)
    {
        foreach (var n in nodes)
        {
            if (n is SourceNode s) s.PublishWires([]);
            n.RequestStop();
        }
        long deadline = Environment.TickCount64 + timeoutMs;
        foreach (var n in nodes) n.Stop((int)Math.Max(1, deadline - Environment.TickCount64));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _snapshot = EngineSnapshot.Empty;
            StopAll(_sources.Values.Cast<AudioNode>().Concat(_outputs.Values).ToList(), StopTimeoutMs);
            _sources.Clear();
            _outputs.Clear();
            _wires.Clear();
            _buses.Clear();
            _links.Clear();
        }
    }
}
