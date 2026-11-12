using Mixer.Core.Config;
using Mixer.Core.Graph;

namespace Mixer.Core.Plugins;

/// <summary>Один источник приходит в один выход двумя путями с разной задержкой плагинов: звук звенит (фазовый сдвиг).</summary>
public sealed record LatencyMismatch(string OutputId, string SourceId, IReadOnlyList<string> Fast, IReadOnlyList<string> Slow, double DifferenceMs);

/// <summary>
/// Задержка плагинов на путях «источник → шины → выход» (VST, «Задержка»). Компенсации между путями нет, поэтому
/// разница больше <see cref="ThresholdMs"/> — предупреждение у выхода с обоими путями.
/// </summary>
public static class PluginLatency
{
    public const double ThresholdMs = 2;

    /// <summary>Задержка плагинов каждого узла, мс, по состоянию слотов.</summary>
    public static IReadOnlyDictionary<string, double> ByNode(IEnumerable<PluginSlotStatus> slots) =>
        slots.Where(s => s.Rate > 0).GroupBy(s => s.NodeId)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.LatencySamples * 1000.0 / s.Rate));

    /// <summary>Все пути по включённым проводам: узлы пути и сумма задержек их плагинов, мс.</summary>
    public static IEnumerable<(IReadOnlyList<string> Path, double Ms)> Paths(GraphConfig graph, IReadOnlyDictionary<string, double> nodeMs)
    {
        var paths = new GraphPaths(graph.Nodes, graph.Wires.Where(w => w.Enabled));
        foreach (var src in graph.Nodes.Where(n => n.IsSource))
        {
            var stack = new Stack<(string Node, List<string> Path)>();
            stack.Push((src.Id, [src.Id]));
            while (stack.Count > 0)
            {
                var (node, path) = stack.Pop();
                if (paths.Node(node)?.IsOutput == true)
                {
                    yield return (path, path.Sum(id => nodeMs.GetValueOrDefault(id)));
                    continue;
                }
                foreach (string next in paths.Next(node))
                    if (!path.Contains(next) && path.Count <= GraphPaths.MaxBusDepth + 1) stack.Push((next, [.. path, next]));
            }
        }
    }

    /// <summary>Наибольшая задержка плагинов от источника до выхода (для суммарной задержки в шапке).</summary>
    public static IReadOnlyDictionary<(string Source, string Output), double> Max(GraphConfig graph, IReadOnlyDictionary<string, double> nodeMs)
    {
        var max = new Dictionary<(string, string), double>();
        foreach (var (path, ms) in Paths(graph, nodeMs))
        {
            var key = (path[0], path[^1]);
            max[key] = Math.Max(max.GetValueOrDefault(key), ms);
        }
        return max;
    }

    public static IReadOnlyList<LatencyMismatch> Mismatches(GraphConfig graph, IReadOnlyDictionary<string, double> nodeMs)
    {
        if (nodeMs.Count == 0) return [];
        return Paths(graph, nodeMs).GroupBy(p => (p.Path[0], p.Path[^1]))
            .Select(g =>
            {
                var fast = g.MinBy(p => p.Ms);
                var slow = g.MaxBy(p => p.Ms);
                return new LatencyMismatch(g.Key.Item2, g.Key.Item1, fast.Path, slow.Path, slow.Ms - fast.Ms);
            })
            .Where(m => m.DifferenceMs > ThresholdMs)
            .ToList();
    }
}
