using Mixer.Core.Config;

namespace Mixer.Core.Graph;

public enum GraphChangeKind
{
    None,
    /// <summary>Только позиции и цвета: движок не трогается.</summary>
    Layout,
    /// <summary>Усиления, mute, моно, баланс, имена, включённость проводов: снимок пересобирается без опроса устройств и процессов.</summary>
    Parameters,
    /// <summary>Узлы, устройства, приложения, провода, заглушка: устройства и процессы перечитываются.</summary>
    Structure,
}

/// <summary>Что поменялось между двумя графами — от этого зависит, сколько работы делает хост.</summary>
public static class GraphChange
{
    public static GraphChangeKind Classify(GraphConfig a, GraphConfig b)
    {
        if (!string.Equals(a.SinkEndpoint, b.SinkEndpoint, StringComparison.OrdinalIgnoreCase)) return GraphChangeKind.Structure;
        if (a.Nodes.Count != b.Nodes.Count || a.Wires.Count != b.Wires.Count) return GraphChangeKind.Structure;
        // дубли с той же длиной списка скрыли бы пропавший элемент
        if (b.Nodes.Select(n => n.Id).Distinct().Count() != b.Nodes.Count
            || b.Wires.Select(w => (w.From, w.To)).Distinct().Count() != b.Wires.Count)
            return GraphChangeKind.Structure;

        var byId = new Dictionary<string, NodeConfig>(StringComparer.Ordinal);
        foreach (var n in a.Nodes) byId.TryAdd(n.Id, n);
        var kind = GraphChangeKind.None;
        foreach (var n in b.Nodes)
        {
            if (!byId.TryGetValue(n.Id, out var old)) return GraphChangeKind.Structure;
            if (old == n) continue;
            if (old.Kind != n.Kind || old.EndpointId != n.EndpointId || old.EndpointName != n.EndpointName || old.DefaultRole != n.DefaultRole
                || old.Exe != n.Exe || old.TakeOver != n.TakeOver || old.DirectRoute != n.DirectRoute || old.LowLatency != n.LowLatency
                || old.IncludeOwnNodes != n.IncludeOwnNodes || !(old.Exclude ?? []).SequenceEqual(n.Exclude ?? [], StringComparer.OrdinalIgnoreCase)
                || old.Cable != n.Cable || !(old.MicFor ?? []).SequenceEqual(n.MicFor ?? [], StringComparer.OrdinalIgnoreCase))
                return GraphChangeKind.Structure;
            var stripped = n with { X = old.X, Y = old.Y, Color = old.Color, Exclude = old.Exclude, MicFor = old.MicFor };
            kind = Max(kind, stripped == old ? GraphChangeKind.Layout : GraphChangeKind.Parameters);
        }

        var wires = new Dictionary<(string, string), WireConfig>();
        foreach (var w in a.Wires) wires.TryAdd((w.From, w.To), w);
        foreach (var w in b.Wires)
        {
            if (!wires.TryGetValue((w.From, w.To), out var old)) return GraphChangeKind.Structure;
            if (old != w) kind = Max(kind, GraphChangeKind.Parameters);
        }
        return kind;
    }

    private static GraphChangeKind Max(GraphChangeKind x, GraphChangeKind y) => (GraphChangeKind)Math.Max((int)x, (int)y);
}
