using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Mixer.App.ViewModels;
using Mixer.Audio.Dsp;

namespace Mixer.App.Ui;

/// <summary>
/// Кривая EQ в панели обработки: сетка 20 Гц – 20 кГц и ±18 дБ, суммарная АЧХ (те же формулы RBJ, что в движке,
/// на 48 кГц) и точки полос. Точку можно тащить (частота и усиление), колесо над ней меняет добротность, правый
/// клик — тип полосы, двойной клик — 0 дБ. Точка ФВЧ двигается только по частоте.
/// </summary>
public sealed class EqCurve : FrameworkElement
{
    private const double Rate = 48000;
    private const double FMin = 20, FMax = 20000, GMax = EqSettings.MaxGainDb;
    private const double PadTop = 6, PadBottom = 20;
    private const double PointR = 10;
    /// <summary>Индекс «полосы» ФВЧ в жестах.</summary>
    private const int HpIndex = -1;

    private static readonly Brush Bg = Freeze(new SolidColorBrush(Color.FromRgb(0x17, 0x19, 0x1C)));
    private static readonly Pen Grid = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x30, 0x36)), 1));
    private static readonly Pen Zero = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x46)), 1));
    private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x4F, 0xA3, 0xFF)));
    private static readonly Brush Faint = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x71, 0x78)));
    private static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6)));
    private static readonly Brush PointFill = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x25)));
    private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface AxisFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private ProcessingViewModel? _vm;
    private int? _drag;
    private int? _hover;

    public EqCurve()
    {
        Height = 190;
        Cursor = Cursors.Arrow;
        Focusable = false;
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.CurveChanged -= InvalidateVisual;
            _vm = e.NewValue as ProcessingViewModel;
            if (_vm is not null) _vm.CurveChanged += InvalidateVisual;
            InvalidateVisual();
        };
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    private static Pen FreezePen(Pen p)
    {
        p.Freeze();
        return p;
    }

    private Rect Plot => new(0, PadTop, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight - PadTop - PadBottom));

    private double X(double f) => Plot.Left + Math.Log(f / FMin) / Math.Log(FMax / FMin) * Plot.Width;
    private double Y(double db) => Plot.Top + (1 - (Math.Clamp(db, -GMax - 6, GMax + 6) + GMax) / (2 * GMax)) * Plot.Height;
    private double FreqAt(double x) => FMin * Math.Pow(FMax / FMin, Math.Clamp((x - Plot.Left) / Plot.Width, 0, 1));
    private double DbAt(double y) => Math.Clamp((1 - (y - Plot.Top) / Plot.Height) * 2 * GMax - GMax, -GMax, GMax);

    /// <summary>Фильтры для кривой — как их соберёт движок.</summary>
    private List<BiquadCoeffs> Filters(ChainSettings c)
    {
        var list = new List<BiquadCoeffs>();
        if (c.Eq.HighPass) list.Add(BiquadCoeffs.Design(BiquadKind.HighPass, c.Eq.HighPassHz, 0, 0.70710678, Rate));
        foreach (var b in c.Eq.Bands)
        {
            if (b.Type == EqBandType.Off) continue;
            var kind = b.Type switch { EqBandType.LowShelf => BiquadKind.LowShelf, EqBandType.HighShelf => BiquadKind.HighShelf, _ => BiquadKind.Peak };
            list.Add(BiquadCoeffs.Design(kind, b.FreqHz, b.GainDb, b.Q, Rate));
        }
        return list;
    }

    /// <summary>Где на кривой точка: пик — на своём усилении, полка — на половине (там её частота), ФВЧ — на −3 дБ.</summary>
    private Point PointOf(ChainSettings c, int index)
    {
        if (index == HpIndex) return new Point(X(c.Eq.HighPassHz), Y(-3));
        var b = c.Eq.Bands[index];
        double db = b.Type == EqBandType.Peak ? b.GainDb : b.GainDb / 2;
        return new Point(X(b.FreqHz), Y(db));
    }

    private IEnumerable<int> Points(ChainSettings c)
    {
        if (c.Eq.HighPass) yield return HpIndex;
        for (int i = 0; i < c.Eq.Bands.Count; i++)
            if (c.Eq.Bands[i].Type != EqBandType.Off) yield return i;
    }

    private int? HitPoint(Point p)
    {
        if (_vm is null) return null;
        var c = _vm.Chain;
        int? best = null;
        double bestD = PointR + 4;
        foreach (int i in Points(c))
        {
            double d = (PointOf(c, i) - p).Length;
            if (d < bestD)
            {
                best = i;
                bestD = d;
            }
        }
        return best;
    }

    protected override HitTestResult HitTestCore(PointHitTestParameters p) => new PointHitTestResult(this, p.HitPoint);

    protected override void OnRender(DrawingContext dc)
    {
        var r = Plot;
        dc.DrawRoundedRectangle(Bg, null, r, 6, 6);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (double f in new double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 })
            dc.DrawLine(Grid, new Point(X(f), r.Top), new Point(X(f), r.Bottom));
        foreach (double db in new double[] { -12, -6, 6, 12 })
            dc.DrawLine(Grid, new Point(r.Left, Y(db)), new Point(r.Right, Y(db)));
        dc.DrawLine(Zero, new Point(r.Left, Y(0)), new Point(r.Right, Y(0)));
        foreach (var (f, text) in new[] { (20.0, Fmt.Hz(20)), (200.0, Fmt.Hz(200)), (2000.0, Fmt.Hz(2000)), (20000.0, Fmt.Hz(20000)) })
        {
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, AxisFace, 11, Muted, dpi);
            double x = Math.Clamp(X(f) - (f == 20 ? 0 : f == 20000 ? ft.Width : ft.Width / 2), 0, Math.Max(0, ActualWidth - ft.Width));
            dc.DrawText(ft, new Point(x, r.Bottom + 4));
        }
        if (_vm is null) return;
        var c = _vm.Chain;
        bool on = c.Eq.Enabled;

        // суммарная АЧХ
        var filters = Filters(c);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            int steps = Math.Max(64, (int)r.Width);
            for (int i = 0; i <= steps; i++)
            {
                double x = r.Left + r.Width * i / steps;
                double f = FreqAt(x);
                double db = 0;
                foreach (var b in filters) db += b.MagnitudeDb(f, Rate);
                var pt = new Point(x, Y(db));
                if (i == 0) ctx.BeginFigure(pt, false, false);
                else ctx.LineTo(pt, true, false);
            }
        }
        geo.Freeze();
        dc.PushClip(new RectangleGeometry(r, 6, 6));
        dc.DrawGeometry(null, new Pen(on ? Accent : Faint, 2), geo);
        dc.Pop();

        foreach (int i in Points(c))
        {
            var p = PointOf(c, i);
            bool hot = _hover == i || _drag == i;
            dc.DrawEllipse(PointFill, new Pen(on ? Accent : Faint, hot ? 2.5 : 2), p, PointR, PointR);
            string label = i == HpIndex ? "HP" : (i + 1).ToString(CultureInfo.InvariantCulture);
            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, i == HpIndex ? 9 : 11, Brushes.White, dpi);
            dc.DrawText(ft, new Point(p.X - ft.Width / 2, p.Y - ft.Height / 2));
        }
    }

    // ── мышь ──

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var hit = HitPoint(e.GetPosition(this));
        if (hit is null || _vm is null) return;
        e.Handled = true;
        if (e.ClickCount == 2 && hit != HpIndex)
        {
            int i = hit.Value;
            _vm.Bands[i].Gain.Value = 0;
            _vm.EndEdit();
            return;
        }
        _drag = hit;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag is { } i && _vm is not null)
        {
            double f = FreqAt(p.X);
            if (i == HpIndex)
            {
                _vm.HighPass.Value = Math.Round(Math.Clamp(f, EqSettings.MinHighPass, EqSettings.MaxHighPass));
            }
            else
            {
                var b = _vm.Chain.Eq.Bands[i];
                double db = DbAt(p.Y) * (b.Type == EqBandType.Peak ? 1 : 2);
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) db = b.GainDb; // Shift — только частота
                f = f >= 1000 ? Math.Round(f / 10) * 10 : Math.Round(f);
                _vm.DragBand(i, f, Math.Round(Math.Clamp(db, -GMax, GMax), 1));
            }
            return;
        }
        var hover = HitPoint(p);
        if (hover != _hover)
        {
            _hover = hover;
            Cursor = hover is null ? Cursors.Arrow : Cursors.SizeAll;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag is null) return;
        _drag = null;
        ReleaseMouseCapture();
        _vm?.EndEdit();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover is null) return;
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var hit = HitPoint(e.GetPosition(this));
        if (hit is null || hit == HpIndex || _vm is null) return;
        _vm.NudgeQ(hit.Value, Math.Sign(e.Delta));
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        var hit = HitPoint(e.GetPosition(this));
        if (hit is null || hit == HpIndex || _vm is null) return;
        var menu = BandTypeMenu(_vm.Bands[hit.Value]);
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Меню типа полосы: пик, низкая полка, высокая полка, выкл.</summary>
    public static ContextMenu BandTypeMenu(EqBandViewModel band)
    {
        var menu = new ContextMenu();
        foreach (var t in new[] { EqBandType.Peak, EqBandType.LowShelf, EqBandType.HighShelf, EqBandType.Off })
        {
            var mi = new MenuItem { Header = Fmt.BandType(t), IsCheckable = true, IsChecked = band.Type == t };
            mi.Click += (_, _) => band.SetType(t);
            menu.Items.Add(mi);
        }
        return menu;
    }
}

/// <summary>
/// Полоса индикатора в панели обработки: гейт (огибающая детектора на шкале −80…0 dBFS с засечкой порога) или
/// подавление компрессора (жёлтая полоса справа, 0…24 дБ). Перерисовывается по такту, когда изменилось состояние.
/// </summary>
public sealed class ChainBar : FrameworkElement
{
    public static readonly DependencyProperty MeterProperty = DependencyProperty.Register(nameof(Meter), typeof(ChainMeterState), typeof(ChainBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThresholdDbProperty = DependencyProperty.Register(nameof(ThresholdDb), typeof(double), typeof(ChainBar),
        new FrameworkPropertyMetadata(-50.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsReductionProperty = DependencyProperty.Register(nameof(IsReduction), typeof(bool), typeof(ChainBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public ChainMeterState? Meter { get => (ChainMeterState?)GetValue(MeterProperty); set => SetValue(MeterProperty, value); }
    public double ThresholdDb { get => (double)GetValue(ThresholdDbProperty); set => SetValue(ThresholdDbProperty, value); }
    public bool IsReduction { get => (bool)GetValue(IsReductionProperty); set => SetValue(IsReductionProperty, value); }

    private static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromRgb(0x26, 0x29, 0x2E)));
    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0xCC, 0x91)));
    private static readonly Brush Yellow = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC4, 0x4D)));

    private int _seen = -1;

    public ChainBar()
    {
        Height = 6;
        IsHitTestVisible = false;
        Loaded += (_, _) => MeterClock.Render += OnFrame;
        Unloaded += (_, _) => MeterClock.Render -= OnFrame;
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    private void OnFrame()
    {
        if (Meter is { } m && m.Version != _seen) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var m = Meter;
        _seen = m?.Version ?? -1;
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0) return;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), h / 2, h / 2);
        if (IsReduction)
        {
            double rw = Math.Clamp((m?.ReductionDb ?? 0) / 24.0, 0, 1) * w;
            if (rw > 0.5) dc.DrawRoundedRectangle(Yellow, null, new Rect(w - rw, 0, rw, h), h / 2, h / 2);
            return;
        }
        static double Frac(double db) => Math.Clamp((db + 80) / 80, 0, 1);
        double lw = Frac(m?.GateLevelDb ?? -80) * w;
        if (m is { GateLevelDb: > MeterState.FloorDb } && lw > 0.5) dc.DrawRoundedRectangle(Green, null, new Rect(0, 0, lw, h), h / 2, h / 2);
        double tx = Math.Clamp(Frac(ThresholdDb) * w - 1, 0, w - 2);
        dc.DrawRectangle(Yellow, null, new Rect(tx, -3, 2, h + 6));
    }
}
