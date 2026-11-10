using System.Windows;
using System.Windows.Media;

namespace Mixer.App.Ui;

/// <summary>Иконка-штрих из макета: путь в системе координат 24×24, цвет — Foreground.</summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(Icon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    public Icon()
    {
        Width = 18;
        Height = 18;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Data is null) return;
        double s = Math.Min(ActualWidth, ActualHeight) / 24.0;
        var pen = new Pen(Foreground, StrokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.PushTransform(new ScaleTransform(s, s));
        dc.DrawGeometry(null, pen, Data);
        dc.Pop();
    }
}

/// <summary>
/// Стереоиндикатор: две полосы по 4 px, −60…0 dBFS. Зелёный, выше −1 dBFS — красный, жёлтым — сколько срезал
/// ограничитель, засечка — удержанный пик. Перерисовывается по такту <see cref="MeterClock"/>, только если состояние
/// изменилось; привязок на каждый кадр нет.
/// </summary>
public sealed class MeterBar : FrameworkElement
{
    public static readonly DependencyProperty MeterProperty = DependencyProperty.Register(nameof(Meter), typeof(MeterState), typeof(MeterBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public MeterState? Meter { get => (MeterState?)GetValue(MeterProperty); set => SetValue(MeterProperty, value); }

    private static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromRgb(0x26, 0x29, 0x2E)));
    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0xCC, 0x91)));
    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0x8B, 0x82)));
    private static readonly Brush Yellow = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC4, 0x4D)));
    private static readonly Brush Hold = Freeze(new SolidColorBrush(Color.FromRgb(0xC4, 0xC8, 0xCD)));

    private int _seen = -1;

    public MeterBar()
    {
        Height = 11;
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
        var m = Meter;
        if (m is null || m.Version == _seen) return;
        InvalidateVisual();
    }

    private static double Frac(float db) => Math.Clamp((db - MeterState.FloorDb) / -MeterState.FloorDb, 0, 1);

    protected override void OnRender(DrawingContext dc)
    {
        var m = Meter;
        _seen = m?.Version ?? -1;
        double w = ActualWidth;
        if (w <= 0) return;
        const double h = 4, gap = 3;
        for (int ch = 0; ch < 2; ch++)
        {
            double y = ch * (h + gap);
            dc.DrawRoundedRectangle(Track, null, new Rect(0, y, w, h), 2, 2);
            if (m is null) continue;
            float level = ch == 0 ? m.L : m.R;
            float hold = ch == 0 ? m.HoldL : m.HoldR;
            double x = Frac(level) * w;
            double red = Frac(MeterState.RedDb) * w;
            if (x > 0.5)
            {
                dc.DrawRoundedRectangle(Green, null, new Rect(0, y, Math.Min(x, red), h), 2, 2);
                if (x > red) dc.DrawRectangle(Red, null, new Rect(red, y, x - red, h));
            }
            if (m.ReductionDb > 0.1f)
            {
                double rw = Math.Min(w - x, m.ReductionDb / -MeterState.FloorDb * w);
                if (rw > 0.5) dc.DrawRectangle(Yellow, null, new Rect(x, y, rw, h));
            }
            if (hold > MeterState.FloorDb + 1)
            {
                double hx = Math.Clamp(Frac(hold) * w - 1, 0, w - 2);
                dc.DrawRectangle(hold >= MeterState.RedDb ? Red : Hold, null, new Rect(hx, y, 2, h));
            }
        }
    }
}

/// <summary>Роль элемента для обработчика мыши холста: «card», «port», «pill».</summary>
public static class Ui
{
    public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached("Role", typeof(string), typeof(Ui), new PropertyMetadata(null));

    public static string? GetRole(DependencyObject d) => (string?)d.GetValue(RoleProperty);
    public static void SetRole(DependencyObject d, string? value) => d.SetValue(RoleProperty, value);
}
