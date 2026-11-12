using Mixer.Core.Config;

namespace Mixer.Core.Editing;

/// <summary>Гнездо модуля: IN k у шины и выхода (по гнезду на входящий провод плюс одно свободное) или OUT у источника и шины.</summary>
/// <param name="From">IN: чей провод сюда воткнут (null — свободное гнездо).</param>
public sealed record RackJack(string NodeId, bool Input, int Index, double X, double Y, string? From = null);

/// <summary>Модуль в стойке: место, размер и гнёзда.</summary>
public sealed record RackModule(string NodeId, NodeColumn Column, double X, double Y, double Width, double Height,
    IReadOnlyList<RackJack> Inputs, RackJack? Output, int Row);

/// <summary>Патч-кабель: от гнезда OUT (Plug — номер штекера в стопке) к гнезду IN.</summary>
public sealed record RackCable(string From, string To, double AX, double AY, double BX, double BY, int Plug);

public sealed record RackLayoutResult(IReadOnlyDictionary<string, RackModule> Modules, IReadOnlyList<RackCable> Cables, double Width, double Height, int Rows);

/// <summary>
/// Раскладка темы Modular (v2, «Темы»): вычисляется, а не хранится. Модули идут слева направо в порядке колонок:
/// источники → шины → выходы, внутри колонки — по Y из схемы. Не влезает в ширину — ряд переносится. Координаты в
/// graph.json не меняются, кроме Y при перестановке модуля среди соседей (<see cref="MoveTarget"/>).
/// </summary>
public static class RackLayout
{
    public const double ModuleHeight = 430;
    public const double ModuleWidth = 150;
    /// <summary>Шаг гнёзд на шине и выходе; больше трёх гнёзд — модуль шире.</summary>
    public const double JackPitch = 46;
    public const double JackMargin = 30;
    /// <summary>Гнёзда — у нижнего края панели.</summary>
    public const double JackFromBottom = 36;
    public const double ModuleGap = 6;
    /// <summary>Слот плагина — узкий модуль 4HP, пристыкованный справа: стойка шире на столько за слот.</summary>
    public const double PluginWidth = 29;
    /// <summary>Пустое место в стойке между колонками (источники | шины | выходы).</summary>
    public const double ColumnGap = 34;
    /// <summary>Под рядом — место для провисших кабелей.</summary>
    public const double RowGap = 170;
    public const double Left = 24;
    public const double Top = 40;

    /// <summary>Провис кабеля, px: 50 + 0,22 × длина кабеля.</summary>
    public static double Sag(double length) => 50 + 0.22 * length;

    /// <summary>Порядок модулей в стойке: колонки слева направо, внутри — по Y (при равенстве — по X и ID).</summary>
    public static IReadOnlyList<NodeConfig> Order(GraphConfig g) =>
        g.Nodes.GroupBy(n => n.Id).Select(x => x.First())
            .OrderBy(n => n.Column).ThenBy(n => n.Y).ThenBy(n => n.X).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();

    public static RackLayoutResult Compute(GraphConfig g, double width)
    {
        width = Math.Max(width, Left * 2 + ModuleWidth * 2);
        var order = Order(g);
        var rank = order.Select((n, i) => (n.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var byId = order.ToDictionary(n => n.Id);
        // входящие провода по порядку источников в стойке: IN 1 — от самого левого
        var incoming = g.Wires.Where(w => byId.ContainsKey(w.From) && byId.ContainsKey(w.To) && byId[w.From].HasOutPort && byId[w.To].HasInPort)
            .GroupBy(w => w.To).ToDictionary(x => x.Key, x => x.OrderBy(w => rank[w.From]).Select(w => w.From).Distinct().ToList());

        var modules = new Dictionary<string, RackModule>();
        double x = Left, y = Top;
        int row = 0;
        NodeColumn? column = null;
        foreach (var n in order)
        {
            var ins = n.HasInPort ? incoming.GetValueOrDefault(n.Id) ?? [] : [];
            int jacks = (n.HasInPort ? ins.Count + 1 : 0) + (n.HasOutPort ? 1 : 0);
            double fx = PluginWidth * (n.Plugins?.Count ?? 0);
            double w = ModuleWidth + JackPitch * Math.Max(0, jacks - 3) + fx; // больше трёх гнёзд — модуль шире
            if (column is not null && column != n.Column) x += ColumnGap;
            if (x + w > width - Left && x > Left)
            {
                x = Left;
                y += ModuleHeight + RowGap;
                row++;
            }
            column = n.Column;
            double jy = y + ModuleHeight - JackFromBottom;
            var inputs = new List<RackJack>();
            int k = 0;
            double jx = x + JackMargin;
            if (n.HasInPort)
            {
                foreach (string from in ins) inputs.Add(new RackJack(n.Id, true, k++, jx + JackPitch * (inputs.Count), jy, from));
                inputs.Add(new RackJack(n.Id, true, k, jx + JackPitch * inputs.Count, jy));
            }
            RackJack? output = n.HasOutPort
                ? new RackJack(n.Id, false, 0, n.HasInPort ? jx + JackPitch * inputs.Count : x + (w - fx) / 2, jy)
                : null;
            modules[n.Id] = new RackModule(n.Id, n.Column, x, y, w, ModuleHeight, inputs, output, row);
            x += w + ModuleGap;
        }

        var cables = new List<RackCable>();
        var plugs = new Dictionary<string, int>();
        foreach (var w in g.Wires)
        {
            if (!modules.TryGetValue(w.From, out var src) || !modules.TryGetValue(w.To, out var dst) || src.Output is null) continue;
            var jack = dst.Inputs.FirstOrDefault(j => j.From == w.From);
            if (jack is null) continue;
            int plug = plugs.GetValueOrDefault(w.From);
            plugs[w.From] = plug + 1;
            cables.Add(new RackCable(w.From, w.To, src.Output.X, src.Output.Y, jack.X, jack.Y, plug));
        }
        double height = y + ModuleHeight + RowGap;
        double right = modules.Count == 0 ? width : modules.Values.Max(m => m.X + m.Width) + Left;
        return new RackLayoutResult(modules, cables, Math.Max(width, right), height, row + 1);
    }

    /// <summary>
    /// Перестановка модуля среди соседей по колонке: модуль бросили в точке x, y стойки. Возвращает соседа, с которым
    /// модуль меняется местами (их Y меняются), или null, если место не изменилось.
    /// </summary>
    public static string? MoveTarget(RackLayoutResult layout, string nodeId, double x, double y)
    {
        if (!layout.Modules.TryGetValue(nodeId, out var self)) return null;
        RackModule? best = null;
        double bestD = double.MaxValue;
        foreach (var m in layout.Modules.Values)
        {
            if (m.Column != self.Column || m.NodeId == nodeId) continue;
            if (y < m.Y - RowGap / 2 || y > m.Y + m.Height + RowGap / 2) continue;
            double d = Math.Abs(m.X + m.Width / 2 - x);
            if (d < m.Width / 2 && d < bestD)
            {
                best = m;
                bestD = d;
            }
        }
        return best?.NodeId;
    }
}
