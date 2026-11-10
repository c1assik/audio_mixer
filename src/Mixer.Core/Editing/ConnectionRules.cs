using Mixer.Audio.Interop;
using Mixer.Core.Config;
using Mixer.Core.Graph;

namespace Mixer.Core.Editing;

public enum ConnectVerdict
{
    Ok,
    /// <summary>Провод между этой парой узлов уже есть.</summary>
    Exists,
    /// <summary>Оба конца — источники или оба — выходы (граф двудольный).</summary>
    SameSide,
    /// <summary>Провод замкнёт петлю через устройство.</summary>
    Feedback,
    Missing,
}

/// <param name="From">Источник (после нормализации направления).</param>
/// <param name="To">Выход.</param>
public sealed record ConnectCheck(ConnectVerdict Verdict, string From, string To, string? Reason = null)
{
    public bool Ok => Verdict == ConnectVerdict.Ok;
}

/// <summary>
/// Можно ли протянуть провод между двумя узлами. Тянуть можно в обе стороны (от источника к выходу и обратно),
/// направление нормализуется. Петли через устройство ловит то же правило, что и сборка графа (<see cref="FeedbackRules"/>).
/// </summary>
public static class ConnectionRules
{
    public static ConnectCheck Check(GraphConfig g, string a, string b, IReadOnlyList<EndpointInfo> devices)
    {
        var na = GraphEdits.Find(g, a);
        var nb = GraphEdits.Find(g, b);
        if (na is null || nb is null) return new ConnectCheck(ConnectVerdict.Missing, a, b, CoreStrings.Connect_NodeNotFound);
        if (na.IsSource == nb.IsSource)
            return new ConnectCheck(ConnectVerdict.SameSide, a, b,
                na.IsSource ? CoreStrings.Connect_TwoSources : CoreStrings.Connect_TwoOutputs);

        var (src, dst) = na.IsSource ? (na, nb) : (nb, na);
        if (GraphEdits.FindWire(g, src.Id, dst.Id) is not null)
            return new ConnectCheck(ConnectVerdict.Exists, src.Id, dst.Id, Loc.F(CoreStrings.Connect_ExistsFormat, src.Name, dst.Name));

        string? why = WhyForbidden(src, dst, devices);
        return why is null
            ? new ConnectCheck(ConnectVerdict.Ok, src.Id, dst.Id)
            : new ConnectCheck(ConnectVerdict.Feedback, src.Id, dst.Id, why);
    }

    /// <summary>Причина, по которой провод src → dst замкнёт петлю, или null.</summary>
    public static string? WhyForbidden(NodeConfig src, NodeConfig dst, IReadOnlyList<EndpointInfo> devices)
    {
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
