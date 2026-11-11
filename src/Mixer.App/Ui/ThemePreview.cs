using System.Windows;
using System.Windows.Media;

namespace Mixer.App.Ui;

/// <summary>
/// Миниатюра темы в настройках (макет «Theme · settings picker»): две карточки и выход с кривыми проводами
/// (Graphite, Daylight) или три модуля стойки с провисшими кабелями (Modular). Цвета — самой показываемой темы.
/// </summary>
public sealed class ThemePreview : FrameworkElement
{
    public static readonly DependencyProperty ThemeProperty =
        DependencyProperty.Register(nameof(Theme), typeof(string), typeof(ThemePreview), new FrameworkPropertyMetadata(ThemeManager.Graphite, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Theme
    {
        get => (string)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    private static SolidColorBrush B(uint argb) => new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        var clip = new RectangleGeometry(new Rect(0, 0, w, h), 6, 6);
        dc.PushClip(clip);
        if (Theme == ThemeManager.Modular) Modular(dc, w, h);
        else Cards(dc, w, h, Theme == ThemeManager.Daylight);
        dc.Pop();
    }

    private static void Cards(DrawingContext dc, double w, double h, bool light)
    {
        dc.DrawRectangle(light ? B(0xFFF3F4F6) : B(0xFF15171A), null, new Rect(0, 0, w, h));
        var card = light ? B(0xFFFFFFFF) : B(0xFF1E2125);
        var line = new Pen(light ? B(0xFFD9DCE1) : B(0xFF2C3036), 1);
        Rect a = new(w * 0.06, h * 0.16, w * 0.3, h * 0.26), b = new(w * 0.06, h * 0.56, w * 0.3, h * 0.26), o = new(w * 0.64, h * 0.36, w * 0.3, h * 0.26);
        foreach (var r in new[] { a, b, o }) dc.DrawRoundedRectangle(card, line, r, 4, 4);
        void Wire(Rect from, uint color)
        {
            var p0 = new Point(from.Right, from.Top + from.Height / 2);
            var p1 = new Point(o.Left, o.Top + o.Height / 2);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(p0, false, false);
                double dx = (p1.X - p0.X) / 2;
                c.BezierTo(new Point(p0.X + dx, p0.Y), new Point(p1.X - dx, p1.Y), p1, true, false);
            }
            dc.DrawGeometry(null, new Pen(B(color), 2), g);
        }
        Wire(a, light ? 0xFF1565C8 : 0xFF4FA3FF);
        Wire(b, light ? 0xFFB85A10 : 0xFFFF9A4D);
    }

    private static void Modular(DrawingContext dc, double w, double h)
    {
        dc.DrawRectangle(B(0xFF0E0F11), null, new Rect(0, 0, w, h));
        var rail = B(0xFF2B2C30);
        dc.DrawRectangle(rail, null, new Rect(0, h * 0.1, w, 4));
        dc.DrawRectangle(rail, null, new Rect(0, h * 0.82, w, 4));
        double mw = w * 0.17, top = h * 0.14, mh = h * 0.66;
        uint[] panels = [0xFFD3D0C9, 0xFFD3D0C9, 0xFF1D1E21, 0xFFECE3CE];
        double[] xs = [w * 0.05, w * 0.24, w * 0.52, w * 0.76];
        for (int i = 0; i < 4; i++)
        {
            dc.DrawRectangle(B(panels[i]), null, new Rect(xs[i], top, mw, mh));
            dc.DrawEllipse(B(0xFF151617), null, new Point(xs[i] + mw / 2, top + mh * 0.32), mw * 0.28, mw * 0.28);
        }
        void Cable(double x0, double x1, uint color)
        {
            var p0 = new Point(x0, top + mh * 0.85);
            var p1 = new Point(x1, top + mh * 0.85);
            double sag = 10 + 0.22 * Math.Abs(x1 - x0);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(p0, false, false);
                c.QuadraticBezierTo(new Point((x0 + x1) / 2, p0.Y + sag), p1, true, false);
            }
            dc.DrawGeometry(null, new Pen(B(color), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
        }
        Cable(xs[0] + mw / 2, xs[2] + mw * 0.3, 0xFF3D8BFF);
        Cable(xs[1] + mw / 2, xs[2] + mw * 0.7, 0xFFFF8A3D);
        Cable(xs[2] + mw / 2, xs[3] + mw / 2, 0xFFF5C330);
    }
}
