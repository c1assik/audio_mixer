using Mixer.Audio.Engine;
using Mixer.Audio.Interop;
using Mixer.Core.Apps;
using Mixer.Core.Config;
using Mixer.Core.Sys;

namespace Mixer.Core.Graph;

public enum IssueKind
{
    WaitingDevice,
    /// <summary>Приложение узла не запущено: узел ждёт, провода сохраняются.</summary>
    WaitingProcess,
    Unsupported,
    Invalid,
    Feedback,
    /// <summary>«Весь звук системы» и приложение в режиме «Забрать себе» ведут в один выход: приложение прозвучит дважды.</summary>
    DoubleSound,
    /// <summary>Узел графа слышит заглушку или играет в неё.</summary>
    SinkConflict,
}

public sealed record GraphIssue(IssueKind Kind, string Subject, string Message);

public sealed record BuildResult(
    EngineGraph Graph,
    IReadOnlyList<GraphIssue> Issues,
    IReadOnlyDictionary<string, EndpointInfo> Resolved)
{
    /// <summary>
    /// Узел графа → его источники в движке. У приложения с несколькими корнями (несколько экземпляров exe)
    /// на каждый корень свой захват «id@pid», они суммируются на выходах.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> EngineSources { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    /// «Весь звук системы» без приложений со своим узлом: что захвачено, что убрано и что пропущено (по ID узла).
    /// Нет записи — узел в обычном режиме, один exclude-захват по PID микшера.
    /// </summary>
    public IReadOnlyDictionary<string, SystemPlan> SystemPlans { get; init; } = new Dictionary<string, SystemPlan>();
}

/// <summary>Состояние системы, нужное для узлов «Приложение» и «Весь звук системы».</summary>
public sealed record RuntimeContext
{
    /// <summary>Причина, по которой process loopback недоступен (null — доступен).</summary>
    public string? ProcessLoopbackUnavailable { get; init; }
    /// <summary>PID микшера: «Весь звук системы» исключает его дерево.</summary>
    public uint SelfPid { get; init; } = (uint)Environment.ProcessId;
    /// <summary>Корни запущенных процессов по нормализованному имени exe.</summary>
    public Func<string, IReadOnlyList<uint>> Roots { get; init; } = _ => [];
    /// <summary>Частота выхода по ID эндпоинта (null — неизвестна).</summary>
    public Func<string, int?> OutputRate { get; init; } = _ => null;
    /// <summary>Заглушка, если «Забрать себе» действует (проверена и API доступен); иначе null.</summary>
    public string? ActiveSinkEndpointId { get; init; }
    /// <summary>
    /// Запущен ли Voicemeeter (null — неизвестно, эндпоинты VAIO не блокируются). Без Voicemeeter эндпоинты VAIO
    /// вешают IAudioClient.Initialize и службу Windows Audio (этап 0), поэтому узлы на них ждут его запуска.
    /// </summary>
    public bool? VoicemeeterRunning { get; init; }
    /// <summary>
    /// План «Весь звук системы» без заданных приложений (<see cref="SystemCapture"/>). null — не умеем, остаётся один
    /// exclude-захват, и приложения со своим узлом в нём звучат второй раз (предупреждение DoubleSound).
    /// </summary>
    public Func<IReadOnlyCollection<string>, SystemPlan>? PlanSystem { get; init; }
}

/// <summary>Граф из конфига → снимок маршрутизации для движка: поиск устройств, процессов, проверка проводов.</summary>
public static class GraphBuilder
{
    /// <summary>Частота захвата приложения, если выходы узла на разных частотах (или неизвестны).</summary>
    public const int DefaultCaptureRate = 48000;

    /// <summary>
    /// Поиск устройства: сначала по endpointId, потом по точному endpointName
    /// (после переустановки драйвера или смены USB-порта ID меняется). Узел «по умолчанию» берёт текущее устройство
    /// по умолчанию своей роли.
    /// </summary>
    public static EndpointInfo? Resolve(NodeConfig node, IReadOnlyList<EndpointInfo> devices)
    {
        var flow = node.Kind == NodeKind.DeviceInput ? EDataFlow.Capture : EDataFlow.Render;
        if (node.DefaultRole is { } role)
            return devices.FirstOrDefault(d => d.Flow == flow && (role == DefaultRole.Communications ? d.IsDefaultComms : d.IsDefault));
        if (node.EndpointId is { Length: > 0 } id)
        {
            var byId = devices.FirstOrDefault(d => d.Flow == flow && string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
            if (byId is not null) return byId;
        }
        if (node.EndpointName is { Length: > 0 } name)
            return devices.FirstOrDefault(d => d.Flow == flow && d.Name == name);
        return null;
    }

    /// <summary>
    /// Частота захвата process loopback: если все выходы узла на одной частоте — она (конвертирует Windows, лишнего
    /// ресемплинга нет), иначе 48 кГц, а провода в выходы 44,1 кГц ресемплируются 48 → 44,1.
    /// </summary>
    public static int CaptureRate(IEnumerable<int?> outputRates)
    {
        var known = outputRates.Where(r => r is > 0).Select(r => r!.Value).Distinct().ToList();
        return known.Count == 1 ? known[0] : DefaultCaptureRate;
    }

    public static string EngineId(NodeConfig node, uint pid, bool perProcess = false) =>
        node.Kind == NodeKind.App || perProcess ? $"{node.Id}@{pid}" : node.Id;

    /// <summary>ID узла графа по ID источника в движке («n2@1234» → «n2»).</summary>
    public static string NodeIdOf(string engineId)
    {
        int at = engineId.LastIndexOf('@');
        return at > 0 ? engineId[..at] : engineId;
    }

    public static BuildResult Build(GraphConfig graph, IReadOnlyList<EndpointInfo> devices, bool allowFeedback = false, RuntimeContext? apps = null)
    {
        apps ??= new RuntimeContext();
        var issues = new List<GraphIssue>();
        var resolved = new Dictionary<string, EndpointInfo>();
        var sources = new List<SourceSpec>();
        var outputs = new List<OutputSpec>();
        var wires = new List<WireSpec>();
        var nodes = new Dictionary<string, NodeConfig>();
        var engineSources = new Dictionary<string, IReadOnlyList<string>>();
        // узлы process loopback: PID захватов (у «Весь звук системы» — PID микшера для exclude)
        var procNodes = new Dictionary<string, IReadOnlyList<uint>>();
        var systemPlans = new Dictionary<string, SystemPlan>();
        bool systemSeen = false;
        // приложения со своим узлом: «Весь звук системы» их не повторяет
        var appExes = graph.Nodes.Where(n => n.Kind == NodeKind.App && !string.IsNullOrWhiteSpace(n.Exe))
            .Select(n => ProcessSnapshot.NormalizeExe(n.Exe!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var n in graph.Nodes)
        {
            if (!nodes.TryAdd(n.Id, n))
            {
                issues.Add(new GraphIssue(IssueKind.Invalid, n.Id, Loc.F(CoreStrings.Issue_DuplicateIdFormat, n.Id)));
                continue;
            }

            if (n.Kind is NodeKind.App or NodeKind.System)
            {
                if (apps.ProcessLoopbackUnavailable is { } why)
                {
                    issues.Add(new GraphIssue(IssueKind.Unsupported, n.Id, Loc.Subject(n.Name, why)));
                    continue;
                }
                if (n.Kind == NodeKind.System)
                {
                    if (systemSeen)
                    {
                        issues.Add(new GraphIssue(IssueKind.Invalid, n.Id, CoreStrings.Issue_SystemOnlyOne));
                        continue;
                    }
                    systemSeen = true;
                    if (appExes.Count > 0 && apps.PlanSystem is { } plan)
                    {
                        var sp = plan(appExes);
                        systemPlans[n.Id] = sp;
                        procNodes[n.Id] = sp.Targets.Select(t => t.Pid).ToList();
                    }
                    else
                    {
                        procNodes[n.Id] = [apps.SelfPid];
                    }
                    continue;
                }
                if (string.IsNullOrWhiteSpace(n.Exe))
                {
                    issues.Add(new GraphIssue(IssueKind.Invalid, n.Id, Loc.Subject(n.Name, CoreStrings.Issue_NoExe)));
                    continue;
                }
                var roots = apps.Roots(ProcessSnapshot.NormalizeExe(n.Exe));
                procNodes[n.Id] = roots;
                if (roots.Count == 0)
                    issues.Add(new GraphIssue(IssueKind.WaitingProcess, n.Id, Loc.Subject(n.Name, Loc.F(CoreStrings.Issue_AppNotRunningFormat, ProcessSnapshot.NormalizeExe(n.Exe)))));
                continue;
            }

            var ep = Resolve(n, devices);
            if (ep is null)
            {
                issues.Add(new GraphIssue(IssueKind.WaitingDevice, n.Id, n.DefaultRole is { } role
                    ? Loc.Subject(n.Name, Loc.F(CoreStrings.Issue_NoDefaultFormat, DefaultDeviceText(n.Kind, role)))
                    : Loc.Subject(n.Name, Loc.F(CoreStrings.Issue_DeviceNotFoundFormat, n.EndpointName ?? n.EndpointId))));
                continue;
            }
            if (ep.IsVoicemeeter && apps.VoicemeeterRunning == false)
            {
                issues.Add(new GraphIssue(IssueKind.WaitingDevice, n.Id,
                    Loc.Subject(n.Name, Loc.F(CoreStrings.Issue_VoicemeeterOffFormat, ep.Name))));
                continue;
            }
            resolved[n.Id] = ep;

            switch (n.Kind)
            {
                case NodeKind.DeviceInput:
                    sources.Add(new SourceSpec(n.Id, SourceKind.DeviceInput, ep.Id, n.GainDb, n.Muted, n.Mono ?? false, n.Balance ?? 0));
                    engineSources[n.Id] = [n.Id];
                    break;
                case NodeKind.Loopback:
                    sources.Add(new SourceSpec(n.Id, SourceKind.Loopback, ep.Id, n.GainDb, n.Muted, n.Mono ?? false, n.Balance ?? 0));
                    engineSources[n.Id] = [n.Id];
                    break;
                case NodeKind.DeviceOutput:
                    outputs.Add(new OutputSpec(n.Id, ep.Id, n.GainDb, n.Muted));
                    break;
            }
        }

        var seen = new HashSet<(string, string)>();
        var valid = new List<WireConfig>();
        foreach (var w in graph.Wires)
        {
            string subject = $"{w.From} → {w.To}";
            if (!nodes.TryGetValue(w.From, out var from) || !nodes.TryGetValue(w.To, out var to))
            {
                issues.Add(new GraphIssue(IssueKind.Invalid, subject, CoreStrings.Issue_WireMissingNode));
                continue;
            }
            if (!from.IsSource || !to.IsOutput)
            {
                issues.Add(new GraphIssue(IssueKind.Invalid, subject, CoreStrings.Issue_WireDirection));
                continue;
            }
            if (!seen.Add((w.From, w.To)))
            {
                issues.Add(new GraphIssue(IssueKind.Invalid, subject, CoreStrings.Issue_WireDuplicate));
                continue;
            }
            resolved.TryGetValue(w.From, out var epFrom);
            resolved.TryGetValue(w.To, out var epTo);
            string? why = FeedbackRules.WhyForbidden(from, epFrom, epTo, devices);
            if (why is not null)
            {
                issues.Add(new GraphIssue(IssueKind.Feedback, subject, why));
                if (!allowFeedback) continue;
            }
            if (epTo is null) continue; // выход ждёт устройство, провод остаётся в конфиге
            if (procNodes.TryGetValue(w.From, out var pids))
            {
                if (pids.Count > 0) valid.Add(w); // приложение не запущено — провод ждёт вместе с узлом
                continue;
            }
            if (epFrom is null) continue;
            valid.Add(w);
        }

        // ── источники process loopback: частота по выходам их проводов ──
        foreach (var (id, pids) in procNodes)
        {
            if (pids.Count == 0) continue;
            var n = nodes[id];
            int rate = CaptureRate(valid.Where(w => w.From == id).Select(w => apps.OutputRate(resolved[w.To].Id)));
            bool perProcess = systemPlans.ContainsKey(id);
            // «Весь звук системы» без приложений — набор include-захватов, как у узла «Приложение»
            var kind = n.Kind == NodeKind.System && !perProcess ? SourceKind.System : SourceKind.App;
            var ids = new List<string>();
            foreach (uint pid in pids)
            {
                string eid = EngineId(n, pid, perProcess);
                ids.Add(eid);
                sources.Add(new SourceSpec(eid, kind, "", n.GainDb, n.Muted, n.Mono ?? false, n.Balance ?? 0, pid, rate));
            }
            engineSources[id] = ids;
        }

        foreach (var w in valid)
            foreach (string src in engineSources.GetValueOrDefault(w.From, []))
                wires.Add(new WireSpec(src, w.To, w.GainDb, w.Enabled));

        AddDoubleSoundWarnings(graph, nodes, systemPlans, issues);
        AddTakeOverWarnings(graph, nodes, resolved, devices, apps, issues);

        return new BuildResult(new EngineGraph(sources, outputs, wires), issues, resolved) { EngineSources = engineSources, SystemPlans = systemPlans };
    }

    /// <summary>«устройства записи по умолчанию», «устройства связи для воспроизведения по умолчанию» и т. п. (родительный падеж).</summary>
    public static string DefaultDeviceText(NodeKind kind, DefaultRole role) =>
        (kind == NodeKind.DeviceInput, role) switch
        {
            (true, DefaultRole.Communications) => CoreStrings.Issue_DefaultCaptureComms,
            (true, _) => CoreStrings.Issue_DefaultCapture,
            (false, DefaultRole.Communications) => CoreStrings.Issue_DefaultRenderComms,
            _ => CoreStrings.Issue_DefaultRender,
        };

    /// <summary>Приложения с «Забрать себе» по умолчанию включены (спецификация).</summary>
    public static bool TakesOver(NodeConfig n) => n.Kind == NodeKind.App && (n.TakeOver ?? true) && !string.IsNullOrWhiteSpace(n.Exe);

    /// <summary>
    /// «Весь звук системы» в обычном режиме (один exclude-захват) содержит и приложения со своим узлом: если оба
    /// узла ведут в один выход, приложение прозвучит дважды. Режим без приложений (<see cref="SystemCapture"/>) этого не делает.
    /// </summary>
    private static void AddDoubleSoundWarnings(GraphConfig graph, Dictionary<string, NodeConfig> nodes, Dictionary<string, SystemPlan> plans, List<GraphIssue> issues)
    {
        HashSet<string> OutputsOf(string nodeId) =>
            graph.Wires.Where(w => w.From == nodeId && w.Enabled && nodes.ContainsKey(w.To)).Select(w => w.To).ToHashSet();

        var appNodes = nodes.Values.Where(n => n.Kind == NodeKind.App && !string.IsNullOrWhiteSpace(n.Exe)).ToList();
        foreach (var sys in nodes.Values.Where(n => n.Kind == NodeKind.System && !plans.ContainsKey(n.Id)))
        {
            var sysOut = OutputsOf(sys.Id);
            var twice = appNodes.Where(a => OutputsOf(a.Id).Overlaps(sysOut)).Select(a => a.Name).Distinct().ToList();
            if (twice.Count > 0)
                issues.Add(new GraphIssue(IssueKind.DoubleSound, sys.Id,
                    Loc.F(CoreStrings.Issue_DoubleSoundFormat, sys.Name, string.Join(", ", twice))));
        }
    }

    private static void AddTakeOverWarnings(GraphConfig graph, Dictionary<string, NodeConfig> nodes, Dictionary<string, EndpointInfo> resolved,
        IReadOnlyList<EndpointInfo> devices, RuntimeContext apps, List<GraphIssue> issues)
    {
        string? sink = apps.ActiveSinkEndpointId;
        var takers = nodes.Values.Where(TakesOver).ToList();
        if (sink is null || takers.Count == 0) return;

        var sinkEp = devices.FirstOrDefault(d => string.Equals(d.Id, sink, StringComparison.OrdinalIgnoreCase));
        string sinkName = sinkEp?.Name ?? sink;
        foreach (var (id, ep) in resolved)
        {
            var n = nodes[id];
            if (n.Kind == NodeKind.DeviceOutput && string.Equals(ep.Id, sink, StringComparison.OrdinalIgnoreCase))
                issues.Add(new GraphIssue(IssueKind.SinkConflict, id,
                    Loc.F(CoreStrings.Issue_SinkOutputFormat, n.Name)));
            else if (n.Kind == NodeKind.Loopback && string.Equals(ep.Id, sink, StringComparison.OrdinalIgnoreCase))
                issues.Add(new GraphIssue(IssueKind.SinkConflict, id,
                    Loc.F(CoreStrings.Issue_SinkLoopbackFormat, n.Name, sinkName)));
            else if (n.Kind == NodeKind.DeviceInput && FeedbackRules.PairedCableInputs(ep, devices).Any(d => string.Equals(d.Id, sink, StringComparison.OrdinalIgnoreCase)))
                issues.Add(new GraphIssue(IssueKind.SinkConflict, id,
                    Loc.F(CoreStrings.Issue_SinkInputFormat, n.Name, sinkName)));
        }
    }
}
