using Mixer.Core.Config;

namespace Mixer.Core.Editing;

/// <summary>
/// Геометрия холста: источники в левой колонке, шины в средней, выходы в правой; сигнал идёт слева направо.
/// Узел двигается внутри своей колонки свободно, в чужую не переходит.
/// </summary>
public static class GraphLayout
{
    public const double Width = 1180;
    public const double MinHeight = 660;
    public const double CardWidth = 248;
    /// <summary>Высота карточки в обычном состоянии (по ней раскладываются новые узлы).</summary>
    public const double CardHeight = 118;
    /// <summary>Центр порта от верха карточки.</summary>
    public const double PortY = 59;
    public const double Top = 60;
    public const double Gap = 32;
    public const double SourceX = 40;
    public const double OutputX = Width - CardWidth - 40;
    /// <summary>Шины — посередине между источниками и выходами.</summary>
    public const double BusX = (Width - CardWidth) / 2;
    private const double Margin = 16;
    /// <summary>Сколько шина может сдвинуться от середины в каждую сторону.</summary>
    private const double BusSlack = 70;
    /// <summary>Граница левой зоны (для правого клика по пустому месту): между источниками и шинами.</summary>
    private const double LeftZone = (SourceX + CardWidth + BusX) / 2;
    private const double RightZone = (BusX + CardWidth + OutputX) / 2;

    public static (double Min, double Max) ColumnX(NodeColumn column) => column switch
    {
        NodeColumn.Sources => (Margin, BusX - BusSlack - CardWidth - 24),
        NodeColumn.Buses => (BusX - BusSlack, BusX + BusSlack),
        _ => (BusX + BusSlack + CardWidth + 24, Width - Margin - CardWidth),
    };

    public static (double Min, double Max) ColumnX(bool source) => ColumnX(source ? NodeColumn.Sources : NodeColumn.Outputs);

    public static double ColumnLeft(NodeColumn column) => column switch
    {
        NodeColumn.Sources => SourceX,
        NodeColumn.Buses => BusX,
        _ => OutputX,
    };

    /// <summary>Позиция узла, прижатая к его колонке и к верху холста.</summary>
    public static (double X, double Y) Clamp(NodeColumn column, double x, double y)
    {
        var (min, max) = ColumnX(column);
        return (Math.Round(Math.Clamp(x, min, max)), Math.Round(Math.Max(Top - 16, y)));
    }

    public static (double X, double Y) Clamp(bool source, double x, double y) => Clamp(source ? NodeColumn.Sources : NodeColumn.Outputs, x, y);

    /// <summary>Колонка по точке холста.</summary>
    public static NodeColumn ColumnAt(double x) => x < LeftZone ? NodeColumn.Sources : x < RightZone ? NodeColumn.Buses : NodeColumn.Outputs;

    /// <summary>Колонка по точке холста: левая зона — источники.</summary>
    public static bool IsSourceSide(double x) => ColumnAt(x) == NodeColumn.Sources;

    /// <summary>Место для нового узла: под нижним узлом колонки, либо в точке y, если там свободно.</summary>
    public static (double X, double Y) NextSlot(GraphConfig g, NodeColumn column, double? atY = null)
    {
        double x = ColumnLeft(column);
        var nodes = g.Nodes.Where(n => n.Column == column).ToList();
        if (atY is { } want)
        {
            double y = Math.Max(Top, want - PortY);
            bool free = nodes.All(n => y + CardHeight + Gap / 2 <= n.Y || y >= n.Y + CardHeight + Gap / 2);
            if (free) return Clamp(column, x, y);
        }
        if (nodes.Count == 0) return (x, Top);
        return (x, Math.Round(nodes.Max(n => n.Y) + CardHeight + Gap));
    }

    public static (double X, double Y) NextSlot(GraphConfig g, bool source, double? atY = null) =>
        NextSlot(g, source ? NodeColumn.Sources : NodeColumn.Outputs, atY);

    public static double Height(GraphConfig g) =>
        g.Nodes.Count == 0 ? MinHeight : Math.Max(MinHeight, g.Nodes.Max(n => n.Y) + CardHeight + 160);
}
