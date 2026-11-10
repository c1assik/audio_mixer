using System.Runtime;

namespace Mixer.Audio.Engine;

/// <summary>
/// Источник в снимке маршрутизации. Для process loopback (App, System) EndpointId пуст, ProcessId — цель
/// (или исключаемый процесс), SampleRate — частота захвата (её конвертирует Windows).
/// </summary>
public sealed record SourceSpec(string Id, SourceKind Kind, string EndpointId, float GainDb = 0, bool Muted = false, bool Mono = false, float Balance = 0,
    uint ProcessId = 0, int SampleRate = 0);

/// <summary>Выход в снимке маршрутизации.</summary>
public sealed record OutputSpec(string Id, string EndpointId, float GainDb = 0, bool Muted = false);

/// <summary>Провод в снимке маршрутизации.</summary>
public sealed record WireSpec(string From, string To, float GainDb = 0, bool Enabled = true);

/// <summary>Готовый снимок маршрутизации: движок не знает ни о графе, ни об интерфейсе.</summary>
public sealed record EngineGraph(IReadOnlyList<SourceSpec> Sources, IReadOnlyList<OutputSpec> Outputs, IReadOnlyList<WireSpec> Wires)
{
    public static readonly EngineGraph Empty = new([], [], []);
}

/// <summary>
/// Узлы движка на момент последнего Apply. Читается без блокировки из любого потока (интерфейс забирает пики
/// 30 раз в секунду и не должен ждать Apply, который может стоять до 2 с на остановке зависшего узла).
/// </summary>
public sealed record EngineSnapshot(
    IReadOnlyDictionary<string, SourceNode> Sources,
    IReadOnlyDictionary<string, OutputNode> Outputs,
    IReadOnlyDictionary<(string From, string To), Wire> Wires)
{
    public static readonly EngineSnapshot Empty = new(
        new Dictionary<string, SourceNode>(), new Dictionary<string, OutputNode>(), new Dictionary<(string, string), Wire>());
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
    private readonly Dictionary<(string From, string To), Wire> _wires = new();
    private EngineGraph _last = EngineGraph.Empty;
    private volatile EngineSnapshot _snapshot = EngineSnapshot.Empty;

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
    public Wire? FindWire(string from, string to) { lock (_gate) return _wires.GetValueOrDefault((from, to)); }

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

            // ── источники ──
            var newSources = new Dictionary<string, SourceNode>();
            foreach (var s in graph.Sources)
            {
                if (_sources.TryGetValue(s.Id, out var cur) && cur.Kind == s.Kind && cur.EndpointId == s.EndpointId
                    && cur.ProcessId == s.ProcessId && cur.SampleRate == s.SampleRate && restart?.Invoke(s.Id) != true)
                {
                    newSources[s.Id] = cur;
                }
                else
                {
                    if (cur is not null) toStop.Add(cur);
                    cur = new SourceNode(s.Id, s.Kind, s.EndpointId, Options, s.ProcessId, s.SampleRate);
                    newSources[s.Id] = cur;
                    toStart.Add(cur);
                }
                cur.SetGain(s.GainDb, s.Balance, s.Muted);
                cur.Mono = s.Mono;
            }
            foreach (var (id, node) in _sources)
                if (!newSources.ContainsKey(id)) toStop.Add(node);

            // ── выходы ──
            var newOutputs = new Dictionary<string, OutputNode>();
            foreach (var o in graph.Outputs)
            {
                if (_outputs.TryGetValue(o.Id, out var cur) && cur.EndpointId == o.EndpointId && restart?.Invoke(o.Id) != true)
                {
                    newOutputs[o.Id] = cur;
                }
                else
                {
                    if (cur is not null) toStop.Add(cur);
                    cur = new OutputNode(o.Id, o.EndpointId, Options);
                    newOutputs[o.Id] = cur;
                    toStart.Add(cur);
                }
                cur.SetGain(o.GainDb, 0, o.Muted);
            }
            foreach (var (id, node) in _outputs)
                if (!newOutputs.ContainsKey(id)) toStop.Add(node);

            // ── провода ──
            var newWires = new Dictionary<(string, string), Wire>();
            foreach (var w in graph.Wires)
            {
                if (!newSources.TryGetValue(w.From, out var src) || !newOutputs.TryGetValue(w.To, out var dst)) continue;
                var key = (w.From, w.To);
                if (newWires.ContainsKey(key)) continue; // между парой узлов только один провод
                bool sameNodes = _wires.TryGetValue(key, out var wire)
                                 && _sources.GetValueOrDefault(w.From) == src && _outputs.GetValueOrDefault(w.To) == dst;
                if (!sameNodes) wire = new Wire(w.From, w.To, src.Clock, Options);
                wire!.GainDb = w.GainDb;
                wire.Enabled = w.Enabled;
                newWires[key] = wire;
            }

            // Удаляемые провода сначала гасим фейдом mute (5 мс), чтобы не было щелчка.
            bool faded = false;
            foreach (var (key, wire) in _wires)
            {
                if (newWires.ContainsKey(key) || !wire.Enabled) continue;
                wire.Enabled = false;
                faded = true;
            }
            if (faded) Thread.Sleep(30);

            // ── публикация массивов ──
            foreach (var src in newSources.Values)
                src.PublishWires(newWires.Values.Where(w => newSources[w.FromId] == src).ToArray());
            foreach (var dst in newOutputs.Values)
                dst.PublishWires(newWires.Values.Where(w => newOutputs[w.ToId] == dst).ToArray());

            _sources.Clear();
            foreach (var kv in newSources) _sources[kv.Key] = kv.Value;
            _outputs.Clear();
            foreach (var kv in newOutputs) _outputs[kv.Key] = kv.Value;
            _wires.Clear();
            foreach (var kv in newWires) _wires[kv.Key] = kv.Value;
            _snapshot = new EngineSnapshot(
                new Dictionary<string, SourceNode>(newSources), new Dictionary<string, OutputNode>(newOutputs),
                new Dictionary<(string, string), Wire>(newWires));

            StopAll(toStop, restart is not null ? RestartStopTimeoutMs : StopTimeoutMs);
            foreach (var n in toStart) n.Start();
        }
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
        }
    }
}
