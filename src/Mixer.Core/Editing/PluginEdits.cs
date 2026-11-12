using Mixer.Core.Config;

namespace Mixer.Core.Editing;

/// <summary>
/// Правки слотов плагинов узла (VST, «Место в цепочке»): до двух слотов в группе «до» и «после», до 16 на схему.
/// Порядок слотов группы — порядок в списке <see cref="NodeConfig.Plugins"/>.
/// </summary>
public static class PluginEdits
{
    public const int MaxPerGroup = 2;
    public const int MaxPerGraph = 16;

    public static IReadOnlyList<PluginConfig> Group(NodeConfig n, PluginGroup group) =>
        (n.Plugins ?? []).Where(p => p.Group == group).ToList();

    public static int Count(GraphConfig g) => g.Nodes.Sum(n => n.Plugins?.Count ?? 0);

    /// <summary>Есть место для слота в группе узла (и на схеме).</summary>
    public static bool HasRoom(GraphConfig g, NodeConfig n, PluginGroup group) =>
        Group(n, group).Count < MaxPerGroup && Count(g) < MaxPerGraph;

    /// <summary>Свободный ID слота p1, p2…; <paramref name="taken"/> — ещё занятые (файл состояния прежнего слота).</summary>
    public static string NewSlotId(NodeConfig n, Func<string, bool>? taken = null)
    {
        var used = (n.Plugins ?? []).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        for (int i = 1; ; i++)
        {
            string id = "p" + i;
            if (!used.Contains(id) && taken?.Invoke(id) != true) return id;
        }
    }

    /// <summary>Файл состояния слота, относительно папки graph.json.</summary>
    public static string StatePath(string nodeId, string slotId) => $"plugins/{nodeId}-{slotId}.state";

    /// <summary>Слот в конец своей группы.</summary>
    public static GraphConfig Add(GraphConfig g, string nodeId, PluginConfig p) =>
        GraphEdits.UpdateNode(g, nodeId, n => n with { Plugins = [.. n.Plugins ?? [], p] });

    /// <summary>Другой плагин на месте слота (у нового свой ID и файл состояния).</summary>
    public static GraphConfig Replace(GraphConfig g, string nodeId, string slotId, PluginConfig p) =>
        GraphEdits.UpdateNode(g, nodeId, n => n with { Plugins = (n.Plugins ?? []).Select(x => x.Id == slotId ? p with { Group = x.Group } : x).ToList() });

    public static GraphConfig Update(GraphConfig g, string nodeId, string slotId, Func<PluginConfig, PluginConfig> change) =>
        GraphEdits.UpdateNode(g, nodeId, n => n with { Plugins = (n.Plugins ?? []).Select(x => x.Id == slotId ? change(x) : x).ToList() });

    public static GraphConfig Remove(GraphConfig g, string nodeId, string slotId) =>
        GraphEdits.UpdateNode(g, nodeId, n =>
        {
            var rest = (n.Plugins ?? []).Where(x => x.Id != slotId).ToList();
            return n with { Plugins = rest.Count == 0 ? null : rest };
        });

    /// <summary>Слот на место <paramref name="index"/> в группе <paramref name="group"/> (перетаскивание, «Перенести в другую группу»).</summary>
    public static GraphConfig Move(GraphConfig g, string nodeId, string slotId, PluginGroup group, int index) =>
        GraphEdits.UpdateNode(g, nodeId, n =>
        {
            var list = (n.Plugins ?? []).ToList();
            int from = list.FindIndex(x => x.Id == slotId);
            if (from < 0) return n;
            var slot = list[from] with { Group = group };
            list.RemoveAt(from);
            var inGroup = list.Select((x, i) => (x, i)).Where(t => t.x.Group == group).Select(t => t.i).ToList();
            int at = index < inGroup.Count ? inGroup[Math.Max(0, index)] : inGroup.Count > 0 ? inGroup[^1] + 1 : list.Count;
            list.Insert(at, slot);
            return n with { Plugins = list };
        });
}
