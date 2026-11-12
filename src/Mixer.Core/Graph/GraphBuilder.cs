using Mixer.Audio.Dsp;
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
    /// <summary>«Весь звук системы» и приложение со своим узлом ведут в один выход: приложение прозвучит дважды.</summary>
    DoubleSound,
    /// <summary>Узел графа слышит заглушку или играет в неё.</summary>
    SinkConflict,
}

public sealed record GraphIssue(IssueKind Kind, string Subject, string Message);

/// <summary>
/// Прямой маршрут узла «Приложение»: звук не захватывается, приложению назначен выход (IAudioPolicyConfig), уровень и
/// mute — громкостью его сессии. GainDb — итог: узел + провод + выход (не выше 0 дБ).
/// </summary>
public sealed record DirectRoute(string NodeId, string Exe, string OutputId, string EndpointId, float GainDb, bool Muted)
{
    /// <summary>Громкость сессии, 0…1 (mute ставится отдельно).</summary>
    public float Level => MathF.Min(1f, MathF.Pow(10f, GainDb / 20f));

    /// <summary>Громкость сессии надо менять: не 100% или mute.</summary>
    public bool Attenuates => Muted || Level < 1f - 1e-4f;
}

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

    /// <summary>
    /// Узел графа → шина в движке, которая несёт его звук после фейдера: у шины это она сама, у приложения и
    /// «Весь звук системы» с обработкой — неявная шина «id#fx» (захваты сводятся в неё, обработка и фейдер — там).
    /// </summary>
    public IReadOnlyDictionary<string, string> EngineBuses { get; init; } = new Dictionary<string, string>();

    /// <summary>Узлы «Приложение» на прямом маршруте (по ID узла): захватов и проводов в движке у них нет.</summary>
    public IReadOnlyDictionary<string, DirectRoute> DirectRoutes { get; init; } = new Dictionary<string, DirectRoute>();
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
    /// <summary>Прямые маршруты возможны: IAudioPolicyConfig доступен и привязки разрешены.</summary>
    public bool DirectRoutes { get; init; }
    /// <summary>Слоты плагинов узла по его ID (<see cref="Plugins.PluginRack"/>); null — без плагинов.</summary>
    public Func<string, IReadOnlyList<PluginSlot>>? Plugins { get; init; }
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

    /// <summary>Суффикс неявной шины обработки у узла с несколькими захватами.</summary>
    public const string FxSuffix = "#fx";

    /// <summary>ID узла графа по ID источника или шины в движке («n2@1234» → «n2», «n3#fx» → «n3»).</summary>
    public static string NodeIdOf(string engineId)
    {
        if (engineId.EndsWith(FxSuffix, StringComparison.Ordinal)) return engineId[..^FxSuffix.Length];
        int at = engineId.LastIndexOf('@');
        return at > 0 ? engineId[..at] : engineId;
    }

    /// <summary>Цепочка для движка: только если включён хотя бы один блок.</summary>
    private static ChainSettings? ChainOf(NodeConfig n) => n.HasChain ? n.Chain : null;

    public static BuildResult Build(GraphConfig graph, IReadOnlyList<EndpointInfo> devices, bool allowFeedback = false, RuntimeContext? apps = null)
    {
        apps ??= new RuntimeContext();
        var plugins = apps.Plugins;
        IReadOnlyList<PluginSlot> Slots(NodeConfig n) => n.HasPlugins && plugins is not null ? plugins(n.Id) : [];
        var issues = new List<GraphIssue>();
        var resolved = new Dictionary<string, EndpointInfo>();
        var sources = new List<SourceSpec>();
        var outputs = new List<OutputSpec>();
        var wires = new List<WireSpec>();
        var nodes = new Dictionary<string, NodeConfig>();
        var engineSources = new Dictionary<string, IReadOnlyList<string>>();
        var buses = new List<BusSpec>();
        var engineBuses = new Dictionary<string, string>();
        // узлы process loopback: PID захватов (у «Весь звук системы» — PID микшера для exclude)
        var procNodes = new Dictionary<string, IReadOnlyList<uint>>();
        var systemPlans = new Dictionary<string, SystemPlan>();
        var noCapture = new Dictionary<string, string>(); // приложения без process loopback → причина
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

            if (n.IsBus)
            {
                buses.Add(new BusSpec(n.Id, n.GainDb, n.Muted) { Chain = ChainOf(n), Plugins = Slots(n), ClockOutput = n.ClockOutput });
                engineBuses[n.Id] = n.Id;
                continue;
            }

            if (n.Kind is NodeKind.App or NodeKind.System)
            {
                if (apps.ProcessLoopbackUnavailable is { } why)
                {
                    // прямому маршруту захват не нужен: решится, когда будут известны провода и выходы
                    if (n.Kind == NodeKind.App) noCapture[n.Id] = why;
                    else issues.Add(new GraphIssue(IssueKind.Unsupported, n.Id, Loc.Subject(n.Name, why)));
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
                    var exclusions = SystemExclusions(n, appExes);
                    if (exclusions.Count > 0 && apps.PlanSystem is { } plan)
                    {
                        var sp = plan(exclusions);
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
                    sources.Add(new SourceSpec(n.Id, SourceKind.DeviceInput, ep.Id, n.GainDb, n.Muted, n.Mono ?? false, n.Balance ?? 0)
                    {
                        Chain = ChainOf(n),
                        LowLatency = n.LowLatencyFor(ep.Name),
                        Plugins = Slots(n),
                    });
                    engineSources[n.Id] = [n.Id];
                    break;
                case NodeKind.Loopback:
                    sources.Add(new SourceSpec(n.Id, SourceKind.Loopback, ep.Id, n.GainDb, n.Muted, n.Mono ?? false, n.Balance ?? 0) { Chain = ChainOf(n), Plugins = Slots(n) });
                    engineSources[n.Id] = [n.Id];
                    break;
                case NodeKind.DeviceOutput:
                    outputs.Add(new OutputSpec(n.Id, ep.Id, n.GainDb, n.Muted) { Chain = ChainOf(n), LowLatency = n.LowLatencyFor(ep.Name), Plugins = Slots(n) });
                    break;
            }
        }

        // провода, замыкающие цикл через шины (файл поправили руками), не берутся: первый по порядку остаётся
        var seen = new HashSet<(string, string)>();
        var accepted = new List<WireConfig>();
        var cyclePaths = new GraphPaths(nodes.Values, []);
        foreach (var w in graph.Wires)
        {
            string subject = $"{w.From} → {w.To}";
            if (!nodes.TryGetValue(w.From, out var from) || !nodes.TryGetValue(w.To, out var to))
            {
                issues.Add(new GraphIssue(IssueKind.Invalid, subject, CoreStrings.Issue_WireMissingNode));
                continue;
            }
            if (!from.HasOutPort || !to.HasInPort || w.From == w.To)
            {
                issues.Add(new GraphIssue(IssueKind.Invalid, subject, CoreStrings.Issue_WireDirection));
                continue;
            }
            if (!seen.Add((w.From, w.To)))
            {
                issues.Add(new GraphIssue(IssueKind.Invalid, subject, CoreStrings.Issue_WireDuplicate));
                continue;
            }
            if (cyclePaths.Path(w.To, w.From) is { } back)
            {
                issues.Add(new GraphIssue(IssueKind.Invalid, subject, Loc.F(CoreStrings.Connect_CycleFormat, cyclePaths.Describe(back.Prepend(w.From)))));
                continue;
            }
            cyclePaths.Add(w.From, w.To);
            accepted.Add(w);
        }

        var paths = new GraphPaths(nodes.Values, accepted);

        // прямые маршруты: такой узел не захватывается, его провод в движок не идёт
        var direct = new Dictionary<string, DirectRoute>();
        foreach (var n in nodes.Values)
            if (DirectRouteOf(n, accepted, nodes, resolved, apps) is { } route)
                direct[n.Id] = route;
        foreach (var (id, why) in noCapture)
            if (!direct.ContainsKey(id))
                issues.Add(new GraphIssue(IssueKind.Unsupported, id, Loc.Subject(nodes[id].Name, why)));

        var valid = new List<WireConfig>();
        foreach (var w in accepted)
        {
            string subject = $"{w.From} → {w.To}";
            var from = nodes[w.From];
            var to = nodes[w.To];
            if (direct.ContainsKey(w.From)) continue;
            resolved.TryGetValue(w.From, out var epFrom);
            if (from.IsSource)
            {
                // петля через устройство на любом пути от источника через шины до выхода
                string? why = paths.Downstream(w.To)
                    .Select(o => resolved.TryGetValue(o.Id, out var epO) ? FeedbackRules.WhyForbidden(from, epFrom, epO, devices) : null)
                    .FirstOrDefault(x => x is not null);
                if (why is not null)
                {
                    issues.Add(new GraphIssue(IssueKind.Feedback, subject, why));
                    if (!allowFeedback) continue;
                }
            }
            if (to.IsOutput && !resolved.ContainsKey(w.To)) continue; // выход ждёт устройство, провод остаётся в конфиге
            if (to.IsBus && !engineBuses.ContainsKey(w.To)) continue;
            if (from.IsBus)
            {
                if (engineBuses.ContainsKey(w.From)) valid.Add(w);
                continue;
            }
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
            if (direct.ContainsKey(id))
            {
                engineSources[id] = [];
                continue;
            }
            if (pids.Count == 0) continue;
            var n = nodes[id];
            int rate = CaptureRate(paths.Downstream(id).Where(o => resolved.ContainsKey(o.Id)).Select(o => apps.OutputRate(resolved[o.Id].Id)));
            bool perProcess = systemPlans.ContainsKey(id);
            // «Весь звук системы» без приложений — набор include-захватов, как у узла «Приложение»
            var kind = n.Kind == NodeKind.System && !perProcess ? SourceKind.System : SourceKind.App;
            var ids = new List<string>();
            // Обработка у узла из нескольких захватов — на их сумме, а не на каждом процессе по отдельности (иначе
            // компрессор «Весь звук системы» жал бы каждое приложение порознь). Захваты сводятся в неявную шину,
            // фейдер и цепочка — на ней. Шина остаётся и при выключенных блоках: включение блока не меняет схему.
            // Плагины — там же: слот один на узел, а не на каждый захват.
            string? fx = n.Chain is not null || n.HasPlugins ? n.Id + FxSuffix : null;
            foreach (uint pid in pids)
            {
                string eid = EngineId(n, pid, perProcess);
                ids.Add(eid);
                sources.Add(fx is null
                    ? new SourceSpec(eid, kind, "", n.GainDb, n.Muted, n.Mono ?? false, n.Balance ?? 0, pid, rate)
                    : new SourceSpec(eid, kind, "", 0, false, n.Mono ?? false, 0, pid, rate));
                if (fx is not null) wires.Add(new WireSpec(eid, fx));
            }
            engineSources[id] = ids;
            if (fx is not null)
            {
                buses.Add(new BusSpec(fx, n.GainDb, n.Muted, n.Balance ?? 0) { Chain = ChainOf(n), Plugins = Slots(n), ClockOutput = n.ClockOutput });
                engineBuses[id] = fx;
            }
        }

        foreach (var w in valid)
        {
            IReadOnlyList<string> from = engineBuses.TryGetValue(w.From, out string? bus) ? [bus] : engineSources.GetValueOrDefault(w.From, []);
            foreach (string src in from)
                wires.Add(new WireSpec(src, w.To, w.GainDb, w.Enabled));
        }

        AddDoubleSoundWarnings(new GraphPaths(nodes.Values, accepted.Where(w => w.Enabled)), nodes, systemPlans, appExes, issues);
        AddTakeOverWarnings(graph, nodes, resolved, devices, apps, issues);

        return new BuildResult(new EngineGraph(sources, outputs, wires) { Buses = buses }, issues, resolved)
        {
            EngineSources = engineSources,
            SystemPlans = systemPlans,
            EngineBuses = engineBuses,
            DirectRoutes = direct,
        };
    }

    /// <summary>
    /// Что исключено из «Весь звук системы»: приложения со своим узлом (если узел не просит их оставить) и его список
    /// исключений. Пусто — узел остаётся одним exclude-захватом по PID микшера.
    /// </summary>
    public static IReadOnlyCollection<string> SystemExclusions(NodeConfig system, IEnumerable<string> appExes)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (system.IncludeOwnNodes != true) set.UnionWith(appExes.Select(ProcessSnapshot.NormalizeExe));
        foreach (string exe in system.Exclude ?? [])
            if (!string.IsNullOrWhiteSpace(exe)) set.Add(ProcessSnapshot.NormalizeExe(exe));
        return set;
    }

    /// <summary>Громкость сессии не поднимается выше 100%: прямой маршрут — только при итоге не выше 0 дБ (запас на округление).</summary>
    public const float MaxDirectGainDb = 0.001f;

    /// <summary>
    /// Прямой маршрут (спецификация v2, «Прямые маршруты»): узел приложения в режиме «Забрать себе» ведёт ровно одним
    /// включённым проводом в выход (не в шину), обработки нет, итоговое усиление не выше 0 дБ. Сверх спецификации:
    /// у узла нет моно и баланса, у выхода нет обработки — громкость сессии их не повторит, а обработку выхода
    /// приложение обошло бы.
    /// </summary>
    public static DirectRoute? DirectRouteOf(NodeConfig n, IEnumerable<WireConfig> wires, IReadOnlyDictionary<string, NodeConfig> nodes,
        IReadOnlyDictionary<string, EndpointInfo> resolved, RuntimeContext ctx)
    {
        if (!ctx.DirectRoutes || !TakesOver(n) || n.DirectRoute == false) return null;
        if (n.HasChain || n.HasPlugins || n.Mono == true || (n.Balance ?? 0) != 0) return null;
        WireConfig? only = null;
        foreach (var w in wires)
        {
            if (w.From != n.Id || !w.Enabled) continue;
            if (only is not null) return null;
            only = w;
        }
        if (only is null || !nodes.TryGetValue(only.To, out var o) || !o.IsOutput || o.HasChain || o.HasPlugins) return null;
        if (!resolved.TryGetValue(o.Id, out var ep)) return null;
        if (string.Equals(ep.Id, ctx.ActiveSinkEndpointId, StringComparison.OrdinalIgnoreCase)) return null;
        float gain = n.GainDb + only.GainDb + o.GainDb;
        if (gain > MaxDirectGainDb) return null;
        return new DirectRoute(n.Id, ProcessSnapshot.NormalizeExe(n.Exe!), o.Id, ep.Id, gain, n.Muted || o.Muted);
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
    /// «Весь звук системы» содержит приложение со своим узлом, если не исключает его: обычный режим (один exclude-захват)
    /// или «не исключать приложения со своим узлом». Если оба узла ведут в один выход, приложение прозвучит дважды.
    /// </summary>
    private static void AddDoubleSoundWarnings(GraphPaths paths, Dictionary<string, NodeConfig> nodes, Dictionary<string, SystemPlan> plans,
        IReadOnlyList<string> appExes, List<GraphIssue> issues)
    {
        HashSet<string> OutputsOf(string nodeId) => paths.Downstream(nodeId).Select(o => o.Id).ToHashSet();

        var appNodes = nodes.Values.Where(n => n.Kind == NodeKind.App && !string.IsNullOrWhiteSpace(n.Exe)).ToList();
        foreach (var sys in nodes.Values.Where(n => n.Kind == NodeKind.System))
        {
            var excluded = plans.ContainsKey(sys.Id)
                ? SystemExclusions(sys, appExes).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>();
            var sysOut = OutputsOf(sys.Id);
            var twice = appNodes.Where(a => !excluded.Contains(ProcessSnapshot.NormalizeExe(a.Exe!)) && OutputsOf(a.Id).Overlaps(sysOut))
                .Select(a => a.Name).Distinct().ToList();
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
            else if (n.Kind == NodeKind.DeviceOutput && sinkEp is not null && FeedbackRules.SameCable(ep, sinkEp))
                issues.Add(new GraphIssue(IssueKind.SinkConflict, id,
                    Loc.F(CoreStrings.Issue_SinkSameCableFormat, n.Name, sinkName)));
            else if (n.Kind == NodeKind.Loopback && string.Equals(ep.Id, sink, StringComparison.OrdinalIgnoreCase))
                issues.Add(new GraphIssue(IssueKind.SinkConflict, id,
                    Loc.F(CoreStrings.Issue_SinkLoopbackFormat, n.Name, sinkName)));
            else if (n.Kind == NodeKind.DeviceInput && FeedbackRules.PairedCableInputs(ep, devices).Any(d => string.Equals(d.Id, sink, StringComparison.OrdinalIgnoreCase)))
                issues.Add(new GraphIssue(IssueKind.SinkConflict, id,
                    Loc.F(CoreStrings.Issue_SinkInputFormat, n.Name, sinkName)));
        }
    }
}
