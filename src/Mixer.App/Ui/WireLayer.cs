using System.Windows;
using System.Windows.Media;
using Mixer.App.ViewModels;

namespace Mixer.App.Ui;

/// <summary>Кривая провода: Безье от порта источника к порту выхода, касательные горизонтальны.</summary>
public static class WireGeometry
{
    private static (Point C1, Point C2) Controls(Point a, Point b)
    {
        double c = Math.Max(60, Math.Abs(b.X - a.X) / 2);
        return (new Point(a.X + c, a.Y), new Point(b.X - c, b.Y));
    }

    public static Point At(Point a, Point b, double t)
    {
        var (c1, c2) = Controls(a, b);
        double u = 1 - t;
        double k0 = u * u * u, k1 = 3 * u * u * t, k2 = 3 * u * t * t, k3 = t * t * t;
        return new Point(k0 * a.X + k1 * c1.X + k2 * c2.X + k3 * b.X, k0 * a.Y + k1 * c1.Y + k2 * c2.Y + k3 * b.Y);
    }

    public static StreamGeometry Build(Point a, Point b)
    {
        var (c1, c2) = Controls(a, b);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(a, false, false);
            ctx.BezierTo(c1, c2, b, true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>Расстояние от точки до кривой и параметр ближайшей точки (по 48 отрезкам).</summary>
    public static (double Distance, double T) Nearest(Point a, Point b, Point p)
    {
        const int steps = 48;
        double best = double.MaxValue, bestT = 0;
        Point prev = a;
        for (int i = 1; i <= steps; i++)
        {
            double t = i / (double)steps;
            Point cur = At(a, b, t);
            var (d, f) = SegmentDistance(prev, cur, p);
            if (d < best)
            {
                best = d;
                bestT = (i - 1 + f) / steps;
            }
            prev = cur;
        }
        return (best, bestT);
    }

    private static (double, double) SegmentDistance(Point a, Point b, Point p)
    {
        Vector ab = b - a, ap = p - a;
        double len2 = ab.LengthSquared;
        double f = len2 < 1e-9 ? 0 : Math.Clamp((ap.X * ab.X + ap.Y * ab.Y) / len2, 0, 1);
        return ((p - (a + ab * f)).Length, f);
    }
}

/// <summary>
/// Все провода холста в одном элементе. Цвет — цвет источника, толщина и яркость — по уровню сигнала; выключенный —
/// серый пунктир, запрещённый (петля) — красный пунктир. Перерисовывается по такту индикаторов, только если уровень
/// заметно изменился, и при правке графа. Этот же элемент — фон холста: он ловит клики по проводам и пустому месту.
/// </summary>
public sealed class WireLayer : FrameworkElement
{
    private static readonly Color Off = Color.FromRgb(0x5F, 0x65, 0x6D);
    private static readonly Color Bad = Color.FromRgb(0xF2, 0x8B, 0x82);
    private readonly Dictionary<(Color, int, int), Pen> _pens = new();
    private readonly Dictionary<WireViewModel, int> _levels = new();
    private MainViewModel? _vm;

    /// <summary>Провод, который сейчас тянут: от точки к курсору.</summary>
    public (Point A, Point B, Color Color, bool Valid)? Ghost { get; private set; }

    public WireLayer()
    {
        Loaded += (_, _) => MeterClock.Render += OnFrame;
        Unloaded += (_, _) => MeterClock.Render -= OnFrame;
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.WiresInvalidated -= InvalidateVisual;
            _vm = e.NewValue as MainViewModel;
            if (_vm is not null) _vm.WiresInvalidated += InvalidateVisual;
            InvalidateVisual();
        };
    }

    public void SetGhost(Point a, Point b, Color color, bool valid)
    {
        Ghost = (a, b, color, valid);
        InvalidateVisual();
    }

    public void ClearGhost()
    {
        Ghost = null;
        InvalidateVisual();
    }

    /// <summary>Уровень в 20 ступенях: провод перерисовывается, только когда ступень сменилась.</summary>
    private static int Step(WireViewModel w) => (int)Math.Round(w.Meter.Level * 20);

    private void OnFrame()
    {
        if (_vm is null) return;
        foreach (var w in _vm.Wires)
        {
            if (_levels.TryGetValue(w, out int seen) && seen == Step(w)) continue;
            InvalidateVisual();
            return;
        }
    }

    protected override HitTestResult HitTestCore(PointHitTestParameters p) => new PointHitTestResult(this, p.HitPoint);

    /// <summary>Провод под точкой (ближе 7 px) и где на нём (0 — у источника, 1 — у выхода).</summary>
    public (WireViewModel? Wire, double T) Hit(Point p)
    {
        if (_vm is null) return (null, 0);
        WireViewModel? best = null;
        double bestD = 7, bestT = 0;
        foreach (var w in _vm.Wires)
        {
            if (w.IsDetached) continue;
            var (d, t) = WireGeometry.Nearest(w.A, w.B, p);
            if (d < bestD)
            {
                best = w;
                bestD = d;
                bestT = t;
            }
        }
        return (best, bestT);
    }

    private Pen PenFor(Color c, double thickness, double opacity, bool dashed)
    {
        var key = (c, (int)Math.Round(thickness * 4), (int)Math.Round(opacity * 20) * 2 + (dashed ? 1 : 0));
        if (_pens.TryGetValue(key, out var pen)) return pen;
        var brush = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(opacity, 0, 1) * 255), c.R, c.G, c.B));
        brush.Freeze();
        pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (dashed) pen.DashStyle = new DashStyle([3.5 / thickness * 1.5, 3.5 / thickness * 1.5], 0);
        pen.Freeze();
        if (_pens.Count > 2000) _pens.Clear();
        _pens[key] = pen;
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        _levels.Clear();
        if (_vm is null) return;
        foreach (var w in _vm.Wires)
        {
            _levels[w] = Step(w);
            if (w.IsDetached) continue;
            var geo = WireGeometry.Build(w.A, w.B);
            double level = w.Meter.Level;
            Color color;
            double thick, opacity;
            bool dashed = false;
            if (w.Forbidden is not null)
            {
                (color, thick, opacity, dashed) = (Bad, 1.5, 0.9, true);
            }
            else if (!w.Enabled)
            {
                (color, thick, opacity, dashed) = (Off, 1.5, 0.9, true);
            }
            else
            {
                color = w.Source.WireColor;
                bool silent = w.Source.Muted || w.Target.Muted;
                thick = 1.5 + 2.5 * level;
                opacity = silent ? 0.3 : w.IsIdle ? 0.4 : 0.55 + 0.45 * level;
            }
            if (w.IsDimmed) opacity *= 0.3;
            if (w.IsSelected || w.IsHovered)
                dc.DrawGeometry(null, PenFor(color, thick + (w.IsSelected ? 7 : 5), w.IsSelected ? 0.35 : 0.18, false), geo);
            dc.DrawGeometry(null, PenFor(color, thick, opacity, dashed), geo);
        }
        if (Ghost is { } g)
            dc.DrawGeometry(null, PenFor(g.Color, 2.5, g.Valid ? 1 : 0.7, !g.Valid), WireGeometry.Build(g.A, g.B));
    }
}
