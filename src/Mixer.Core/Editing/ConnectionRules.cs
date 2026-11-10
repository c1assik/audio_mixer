using Mixer.Audio.Interop;
using Mixer.Core.Config;
using Mixer.Core.Graph;

namespace Mixer.Core.Editing;

public enum ConnectVerdict
{
    Ok,
    /// <summary>Провод между этой парой узлов уже есть.</summary>
    Exists,
    /// <summary>Концы не подходят: два источника, два выхода, узел сам с собой.</summary>
    SameSide,
    /// <summary>Провод замкнёт петлю через устройство.</summary>
    Feedback,
    /// <summary>Провод замкнёт цикл через шины.</summary>
    Cycle,
    /// <summary>Шин на пути станет больше <see cref="GraphPaths.MaxBusDepth"/>.</summary>
    TooDeep,
    Missing,
}

/// <param name="From">Откуда идёт звук (после нормализации направления).</param>
/// <param name="To">Куда.</param>
public sealed record ConnectCheck(ConnectVerdict Verdict, string From, string To, string? Reason = null)
{
    public bool Ok => Verdict == ConnectVerdict.Ok;
}

/// <summary>
/// Можно ли протянуть провод между двумя узлами. Тянуть можно в обе стороны (от источника к выходу и обратно),
/// направление нормализуется; между двумя шинами направление — как передано (from → to). Запрещены: цикл через
/// шины, больше двух шин подряд и петля через устройство на любом пути, который провод создаёт: правило
/// эндпоинтов (<see cref="FeedbackRules"/>) смотрит на все источники выше и все выходы ниже через шины.
/// </summary>
public static class ConnectionRules
{
    public static ConnectCheck Check(GraphConfig g, string a, string b, IReadOnlyList<EndpointInfo> devices)
    {
        var na = GraphEdits.Find(g, a);
        var nb = GraphEdits.Find(g, b);
        if (na is null || nb is null) return new ConnectCheck(ConnectVerdict.Missing, a, b, CoreStrings.Connect_NodeNotFound);
        if (a == b) return new ConnectCheck(ConnectVerdict.SameSide, a, b, CoreStrings.Connect_Self);
        if (na.IsSource && nb.IsSource)
            return new ConnectCheck(ConnectVerdict.SameSide, a, b, CoreStrings.Connect_TwoSources);
        if (na.IsOutput && nb.IsOutput)
            return new ConnectCheck(ConnectVerdict.SameSide, a, b, CoreStrings.Connect_TwoOutputs);

        // направление: в источник и из выхода провод не идёт
        var (src, dst) = !na.HasOutPort || !nb.HasInPort ? (nb, na) : (na, nb);
        if (GraphEdits.FindWire(g, src.Id, dst.Id) is not null)
            return new ConnectCheck(ConnectVerdict.Exists, src.Id, dst.Id, Loc.F(CoreStrings.Connect_ExistsFormat, src.Name, dst.Name));

        var paths = new GraphPaths(g);
        if (paths.Path(dst.Id, src.Id) is { } back)
            return new ConnectCheck(ConnectVerdict.Cycle, src.Id, dst.Id,
                Loc.F(CoreStrings.Connect_CycleFormat, paths.Describe(back.Prepend(src.Id))));
        if (paths.DepthWith(src.Id, dst.Id) > GraphPaths.MaxBusDepth)
            return new ConnectCheck(ConnectVerdict.TooDeep, src.Id, dst.Id, Loc.F(CoreStrings.Connect_TooDeepFormat, GraphPaths.MaxBusDepth));

        paths.Add(src.Id, dst.Id);
        string? why = LoopThrough(paths, src.Id, dst.Id, devices);
        return why is null
            ? new ConnectCheck(ConnectVerdict.Ok, src.Id, dst.Id)
            : new ConnectCheck(ConnectVerdict.Feedback, src.Id, dst.Id, why);
    }

    /// <summary>
    /// Петля через устройство на каком-нибудь пути через провод from → to (ребро уже в paths): источник выше from
    /// слышит выход ниже to. Причина с путём петли или null.
    /// </summary>
    public static string? LoopThrough(GraphPaths paths, string from, string to, IReadOnlyList<EndpointInfo> devices)
    {
        foreach (var s in paths.Upstream(from))
        {
            foreach (var o in paths.Downstream(to))
            {
                string? why = WhyForbidden(s, o, devices);
                if (why is null) continue;
                if (s.Id == from && o.Id == to) return why;
                var head = paths.Path(s.Id, from) ?? [s.Id, from];
                var tail = paths.Path(to, o.Id) ?? [to, o.Id];
                return Loc.F(CoreStrings.Connect_LoopPathFormat, why, paths.Describe(head.Concat(tail)));
            }
        }
        return null;
    }

    /// <summary>
    /// Провод графа сейчас замыкает петлю (устройство по умолчанию сменилось, кабель подключили). Отмечается только
    /// провод от источника: его отключение рвёт петлю, а провода из шин везут и чужой звук.
    /// </summary>
    public static string? WhyWireForbidden(GraphConfig g, WireConfig w, IReadOnlyList<EndpointInfo> devices, GraphPaths? paths = null)
    {
        paths ??= new GraphPaths(g);
        var from = paths.Node(w.From);
        if (from is null || !from.IsSource || paths.Node(w.To) is null) return null;
        return LoopThrough(paths, w.From, w.To, devices);
    }

    /// <summary>Причина, по которой звук источника src в выходе dst замкнёт петлю, или null (без учёта шин).</summary>
    public static string? WhyForbidden(NodeConfig src, NodeConfig dst, IReadOnlyList<EndpointInfo> devices)
    {
        if (!src.IsSource || !dst.IsOutput) return null;
        var epSrc = src.Kind is NodeKind.DeviceInput or NodeKind.Loopback ? GraphBuilder.Resolve(src, devices) : null;
        var epDst = GraphBuilder.Resolve(dst, devices);
        // loopback устройства по умолчанию в выход по умолчанию той же роли — петля, какое бы устройство ни было сейчас
        if (src.Kind == NodeKind.Loopback && src.DefaultRole is { } role && dst.DefaultRole == role)
            return LoopMessage(src, dst.Name);
        if (epDst is null && dst.EndpointId is { Length: > 0 } && src.Kind == NodeKind.Loopback)
        {
            // выход сейчас отключён: loopback того же эндпоинта всё равно петля
            if (src.DefaultRole is null && string.Equals(src.EndpointId, dst.EndpointId, StringComparison.OrdinalIgnoreCase))
                return LoopMessage(src, dst.Name);
            return null;
        }
        if (epDst is null) return null;
        string? why = FeedbackRules.WhyForbidden(src, epSrc, epDst, devices);
        if (why is null) return null;
        return src.Kind == NodeKind.Loopback
            ? LoopMessage(src, dst.Name)
            : Loc.F(CoreStrings.Connect_CableLoopFormat, dst.Name, epDst.Name, src.Name);
    }

    private static string LoopMessage(NodeConfig src, string dstName) =>
        Loc.F(CoreStrings.Connect_LoopFormat, src.Name, dstName);
}
