using Mixer.Core.Config;

namespace Mixer.Core.Editing;

/// <summary>
/// Геометрия холста: источники в левой колонке, выходы в правой, сигнал идёт слева направо.
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
    /// <summary>Расстояние от середины холста до ближнего края колонки.</summary>
    private const double Aisle = 60;
    private const double Margin = 16;

    public static (double Min, double Max) ColumnX(bool source) => source
        ? (Margin, Width / 2 - Aisle - CardWidth)
        : (Width / 2 + Aisle, Width - Margin - CardWidth);

    /// <summary>Позиция узла, прижатая к его колонке и к верху холста.</summary>
    public static (double X, double Y) Clamp(bool source, double x, double y)
    {
        var (min, max) = ColumnX(source);
        return (Math.Round(Math.Clamp(x, min, max)), Math.Round(Math.Max(Top - 16, y)));
    }

    /// <summary>Колонка по точке холста: левая половина — источники.</summary>
    public static bool IsSourceSide(double x) => x < Width / 2;

    /// <summary>Место для нового узла: под нижним узлом колонки, либо в точке y, если там свободно.</summary>
    public static (double X, double Y) NextSlot(GraphConfig g, bool source, double? atY = null)
    {
        double x = source ? SourceX : OutputX;
        var column = g.Nodes.Where(n => n.IsSource == source).ToList();
        if (atY is { } want)
        {
            double y = Math.Max(Top, want - PortY);
            bool free = column.All(n => y + CardHeight + Gap / 2 <= n.Y || y >= n.Y + CardHeight + Gap / 2);
            if (free) return Clamp(source, x, y);
        }
        if (column.Count == 0) return (x, Top);
        return (x, Math.Round(column.Max(n => n.Y) + CardHeight + Gap));
    }

    public static double Height(GraphConfig g) =>
        g.Nodes.Count == 0 ? MinHeight : Math.Max(MinHeight, g.Nodes.Max(n => n.Y) + CardHeight + 160);
}
