using Mixer.Core.Config;

namespace Mixer.Core.Graph;

/// <summary>
/// Пути по проводам графа. С шинами граф больше не двудольный: источник → шина → (шина →) выход.
/// Обходы терпят циклы (в файле, поправленном руками, они могут быть): каждый узел посещается один раз.
/// </summary>
public sealed class GraphPaths
{
    /// <summary>Шин на пути источник → выход не больше этого: глубже схема не читается.</summary>
    public const int MaxBusDepth = 2;

    private readonly Dictionary<string, NodeConfig> _nodes = new();
    private readonly Dictionary<string, List<string>> _out = new();
    private readonly Dictionary<string, List<string>> _in = new();

    public GraphPaths(GraphConfig g) : this(g.Nodes, g.Wires)
    {
    }

    public GraphPaths(IEnumerable<NodeConfig> nodes, IEnumerable<WireConfig> wires)
    {
        foreach (var n in nodes) _nodes.TryAdd(n.Id, n);
        foreach (var w in wires) Add(w.From, w.To);
    }

    public NodeConfig? Node(string id) => _nodes.GetValueOrDefault(id);

    /// <summary>Добавить ребро (для проверки «что будет, если протянуть провод»).</summary>
    public void Add(string from, string to)
    {
        if (!_nodes.ContainsKey(from) || !_nodes.ContainsKey(to)) return;
        if (!_out.TryGetValue(from, out var o)) _out[from] = o = [];
        if (!o.Contains(to)) o.Add(to);
        if (!_in.TryGetValue(to, out var i)) _in[to] = i = [];
        if (!i.Contains(from)) i.Add(from);
    }

    public IReadOnlyList<string> Next(string id) => _out.GetValueOrDefault(id) ?? [];
    public IReadOnlyList<string> Prev(string id) => _in.GetValueOrDefault(id) ?? [];

    /// <summary>Выходы, до которых доходит звук из узла (сам узел, если он выход).</summary>
    public IReadOnlyList<NodeConfig> Downstream(string id) => Walk(id, forward: true).Where(n => n.IsOutput).ToList();

    /// <summary>Источники, звук которых доходит до узла (сам узел, если он источник).</summary>
    public IReadOnlyList<NodeConfig> Upstream(string id) => Walk(id, forward: false).Where(n => n.IsSource).ToList();

    /// <summary>Шины, в которые узел ведёт напрямую или через другие шины.</summary>
    public IReadOnlyList<NodeConfig> BusesBelow(string id) => Walk(id, forward: true).Where(n => n.IsBus && n.Id != id).ToList();

    private IEnumerable<NodeConfig> Walk(string id, bool forward)
    {
        var seen = new HashSet<string> { id };
        var stack = new Stack<string>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            string cur = stack.Pop();
            if (_nodes.TryGetValue(cur, out var n)) yield return n;
            foreach (string next in forward ? Next(cur) : Prev(cur))
                if (seen.Add(next)) stack.Push(next);
        }
    }

    /// <summary>Кратчайший путь from → … → to по проводам (с концами) или null.</summary>
    public IReadOnlyList<string>? Path(string from, string to)
    {
        var prev = new Dictionary<string, string?> { [from] = null };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            string cur = queue.Dequeue();
            if (cur == to)
            {
                var path = new List<string>();
                for (string? x = to; x is not null; x = prev[x]) path.Add(x);
                path.Reverse();
                return path;
            }
            foreach (string next in Next(cur))
            {
                if (prev.ContainsKey(next)) continue;
                prev[next] = cur;
                queue.Enqueue(next);
            }
        }
        return null;
    }

    /// <summary>Шин на самом длинном пути, который приходит в узел (включая его самого, если он шина).</summary>
    public int BusesAbove(string id) => Depth(id, forward: false, new HashSet<string>());

    /// <summary>Шин на самом длинном пути, который выходит из узла (включая его самого, если он шина).</summary>
    public int BusesBelowDepth(string id) => Depth(id, forward: true, new HashSet<string>());

    private int Depth(string id, bool forward, HashSet<string> visiting)
    {
        if (!_nodes.TryGetValue(id, out var n) || !n.IsBus || !visiting.Add(id)) return 0;
        int best = 0;
        foreach (string next in forward ? Next(id) : Prev(id)) best = Math.Max(best, Depth(next, forward, visiting));
        visiting.Remove(id);
        return best + 1;
    }

    /// <summary>Сколько шин окажется на самом длинном пути, если добавить провод from → to.</summary>
    public int DepthWith(string from, string to) => BusesAbove(from) + BusesBelowDepth(to);

    /// <summary>Подпись пути для подсказки: «Звук наушников» → «Игра + чат» → «Наушники».</summary>
    public string Describe(IEnumerable<string> ids) =>
        string.Join(" → ", ids.Select(id => Loc.F(CoreStrings.Path_NameFormat, _nodes.TryGetValue(id, out var n) ? n.Name : id)));
}
