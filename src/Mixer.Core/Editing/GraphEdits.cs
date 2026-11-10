using Mixer.Core.Config;

namespace Mixer.Core.Editing;

/// <summary>Чистые правки графа: каждая возвращает новый GraphConfig, исходный не меняется.</summary>
public static class GraphEdits
{
    /// <summary>Свободный ID узла: n1, n2, …</summary>
    public static string NewNodeId(GraphConfig g)
    {
        var used = g.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        for (int i = 1; ; i++)
            if (!used.Contains("n" + i)) return "n" + i;
    }

    public static NodeConfig? Find(GraphConfig g, string id) => g.Nodes.FirstOrDefault(n => n.Id == id);

    public static WireConfig? FindWire(GraphConfig g, string from, string to) => g.Wires.FirstOrDefault(w => w.From == from && w.To == to);

    public static GraphConfig AddNode(GraphConfig g, NodeConfig node) => g with { Nodes = [.. g.Nodes, node] };

    /// <summary>Удалить узел вместе с его проводами.</summary>
    public static GraphConfig RemoveNode(GraphConfig g, string id) => g with
    {
        Nodes = g.Nodes.Where(n => n.Id != id).ToList(),
        Wires = g.Wires.Where(w => w.From != id && w.To != id).ToList(),
    };

    public static GraphConfig UpdateNode(GraphConfig g, string id, Func<NodeConfig, NodeConfig> change)
    {
        bool hit = false;
        var nodes = g.Nodes.Select(n =>
        {
            if (n.Id != id) return n;
            hit = true;
            return change(n);
        }).ToList();
        return hit ? g with { Nodes = nodes } : g;
    }

    /// <summary>Провод от источника к выходу. Проверка допустимости — <see cref="ConnectionRules"/>; дубль не добавляется.</summary>
    public static GraphConfig Connect(GraphConfig g, string from, string to, float gainDb = 0, bool enabled = true)
    {
        if (FindWire(g, from, to) is not null) return g;
        return g with { Wires = [.. g.Wires, new WireConfig { From = from, To = to, GainDb = gainDb, Enabled = enabled }] };
    }

    public static GraphConfig Disconnect(GraphConfig g, string from, string to) =>
        FindWire(g, from, to) is null ? g : g with { Wires = g.Wires.Where(w => !(w.From == from && w.To == to)).ToList() };

    public static GraphConfig UpdateWire(GraphConfig g, string from, string to, Func<WireConfig, WireConfig> change)
    {
        bool hit = false;
        var wires = g.Wires.Select(w =>
        {
            if (w.From != from || w.To != to) return w;
            hit = true;
            return change(w);
        }).ToList();
        return hit ? g with { Wires = wires } : g;
    }

    /// <summary>Перецепить конец провода на другой узел, сохранив усиление и включённость (на том же месте в списке).</summary>
    public static GraphConfig Reroute(GraphConfig g, string from, string to, string newFrom, string newTo)
    {
        if (from == newFrom && to == newTo) return g;
        if (FindWire(g, from, to) is null || FindWire(g, newFrom, newTo) is not null) return g;
        return UpdateWire(g, from, to, w => w with { From = newFrom, To = newTo });
    }

    public static GraphConfig SetSink(GraphConfig g, string? endpointId) =>
        string.Equals(g.SinkEndpoint, endpointId, StringComparison.OrdinalIgnoreCase) ? g : g with { SinkEndpoint = endpointId };
}
