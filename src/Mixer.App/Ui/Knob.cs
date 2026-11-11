using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Mixer.App.Ui;

/// <summary>
/// Ручка темы Modular (LEVEL на модуле и маленькая ручка провода над гнездом IN): перетаскивание вверх-вниз
/// (200 px — вся шкала, с Shift — в десять раз точнее), колесо (±1 дБ, с Shift ±0,1), стрелки, двойной щелчок — 0 дБ.
/// Значение — дБ (−60…+12). Доступность как у ползунка: RangeBase с автоматизацией RangeValue, фокус виден.
/// </summary>
public sealed class Knob : RangeBase
{
    private Point _down;
    private double _startValue;
    private bool _dragging;

    /// <summary>Перетаскивание закончилось (одна правка истории на всё перетаскивание).</summary>
    public event Action? DragCompleted;
    /// <summary>Двойной щелчок: вернуть 0 дБ.</summary>
    public event Action? ResetRequested;

    public static readonly DependencyProperty SmallProperty = DependencyProperty.Register(nameof(Small), typeof(bool), typeof(Knob),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Маленькая ручка (провод): без шкалы.</summary>
    public bool Small { get => (bool)GetValue(SmallProperty); set => SetValue(SmallProperty, value); }

    static Knob()
    {
        MinimumProperty.OverrideMetadata(typeof(Knob), new FrameworkPropertyMetadata(-60.0));
        MaximumProperty.OverrideMetadata(typeof(Knob), new FrameworkPropertyMetadata(12.0));
        SmallChangeProperty.OverrideMetadata(typeof(Knob), new FrameworkPropertyMetadata(1.0));
        LargeChangeProperty.OverrideMetadata(typeof(Knob), new FrameworkPropertyMetadata(6.0));
        FocusableProperty.OverrideMetadata(typeof(Knob), new FrameworkPropertyMetadata(true));
    }

    public Knob()
    {
        Cursor = Cursors.SizeNS;
        FocusVisualStyle = null;
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        InvalidateVisual();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    /// <summary>Доля шкалы 0…1. 0 дБ — на «двух часах»: −60…0 — три четверти хода, 0…+12 — остальное.</summary>
    public static double Fraction(double db, double min = -60, double max = 12) =>
        db <= 0 ? 0.75 * Math.Clamp((db - min) / (0 - min), 0, 1) : 0.75 + 0.25 * Math.Clamp(db / max, 0, 1);

    public static double FromFraction(double f, double min = -60, double max = 12) =>
        f <= 0.75 ? min + Math.Clamp(f, 0, 0.75) / 0.75 * (0 - min) : Math.Clamp(f - 0.75, 0, 0.25) / 0.25 * max;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount >= 2)
        {
            ResetRequested?.Invoke();
            e.Handled = true;
            return;
        }
        _down = e.GetPosition(this);
        _startValue = Fraction(Value, Minimum, Maximum);
        _dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        double dy = _down.Y - e.GetPosition(this).Y;
        double per = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 2000 : 200;
        double v = FromFraction(_startValue + dy / per, Minimum, Maximum);
        Value = Math.Round(v * 10) / 10;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        DragCompleted?.Invoke();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_dragging) return;
        _dragging = false;
        DragCompleted?.Invoke();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 1;
        Value = Math.Round((Value + Math.Sign(e.Delta) * step) * 10) / 10;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : SmallChange;
        switch (e.Key)
        {
            case Key.Up or Key.Right: Value = Math.Round((Value + step) * 10) / 10; break;
            case Key.Down or Key.Left: Value = Math.Round((Value - step) * 10) / 10; break;
            case Key.PageUp: Value += LargeChange; break;
            case Key.PageDown: Value -= LargeChange; break;
            case Key.Home: Value = 0; break;
            default: return;
        }
        DragCompleted?.Invoke();
        e.Handled = true;
    }

    private static readonly Brush Body = ThemeColors.Brush("KnobBody", Color.FromRgb(0x15, 0x16, 0x17));
    private static readonly Brush Pointer = Brushes.White;

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 4) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = Small ? size / 2 - 1 : size / 2 - 9;
        // шкала: 270° от «семи часов» до «пяти»
        double start = 135, sweep = 270;
        var ink = Foreground ?? Brushes.Black;
        if (!Small)
        {
            var tick = new Pen(ink, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            for (int i = 0; i <= 10; i++)
            {
                double a = (start + sweep * i / 10) * Math.PI / 180;
                var p1 = new Point(c.X + Math.Cos(a) * (r + 4), c.Y + Math.Sin(a) * (r + 4));
                var p2 = new Point(c.X + Math.Cos(a) * (r + (i % 5 == 0 ? 9 : 7)), c.Y + Math.Sin(a) * (r + (i % 5 == 0 ? 9 : 7)));
                dc.DrawLine(tick, p1, p2);
            }
        }
        if (IsKeyboardFocused)
            dc.DrawEllipse(null, new Pen(ThemeColors.Brush("Accent", Color.FromRgb(0xF2, 0xA9, 0x3B)), 2), c, r + 2.5, r + 2.5);
        dc.DrawEllipse(Body, new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1), c, r, r);
        double f = Fraction(Value, Minimum, Maximum);
        double ang = (start + sweep * f) * Math.PI / 180;
        var tip = new Point(c.X + Math.Cos(ang) * r * 0.85, c.Y + Math.Sin(ang) * r * 0.85);
        var from = new Point(c.X + Math.Cos(ang) * r * 0.2, c.Y + Math.Sin(ang) * r * 0.2);
        dc.DrawLine(new Pen(Pointer, Small ? 2 : 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, from, tip);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new KnobPeer(this);

    private sealed class KnobPeer(Knob owner) : RangeBaseAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(Knob);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
    }
}

/// <summary>
/// Индикатор темы Modular: столбик из 10 светодиодов на канал (−54…0 dBFS через 6 дБ): зелёные, два верхних — жёлтый и
/// красный. Перерисовывается по такту <see cref="MeterClock"/>, только если индикатор изменился.
/// </summary>
public sealed class LedMeter : FrameworkElement
{
    public static readonly DependencyProperty MeterProperty = DependencyProperty.Register(nameof(Meter), typeof(MeterState), typeof(LedMeter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public MeterState? Meter { get => (MeterState?)GetValue(MeterProperty); set => SetValue(MeterProperty, value); }

    public const int Leds = 10;
    private static readonly Brush Off = ThemeColors.Brush("LedOff", Color.FromRgb(0x3A, 0x3A, 0x38));
    private static readonly Brush Green = ThemeColors.Brush("Green", Color.FromRgb(0x3D, 0xDC, 0x84));
    private static readonly Brush Yellow = ThemeColors.Brush("Yellow", Color.FromRgb(0xFF, 0xC2, 0x33));
    private static readonly Brush Red = ThemeColors.Brush("Red", Color.FromRgb(0xFF, 0x5A, 0x4E));
    private int _seen = -1;

    public LedMeter()
    {
        Width = 22;
        Height = 80;
        IsHitTestVisible = false;
        Loaded += (_, _) => MeterClock.Render += OnFrame;
        Unloaded += (_, _) => MeterClock.Render -= OnFrame;
    }

    private void OnFrame()
    {
        if (Meter is { } m && m.Version != _seen) InvalidateVisual();
    }

    /// <summary>Сколько светодиодов горит при уровне db: порог k-го (снизу, с нуля) — −54 + 6k дБ.</summary>
    public static int Lit(float db)
    {
        int n = 0;
        for (int k = 0; k < Leds; k++)
            if (db >= -54 + 6 * k - 0.01f) n = k + 1;
        return db <= MeterState.FloorDb + 0.5f ? 0 : n;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var m = Meter;
        _seen = m?.Version ?? -1;
        double w = ActualWidth, h = ActualHeight;
        double cw = (w - 2) / 2, ch = (h - (Leds - 1) * 2) / Leds;
        for (int side = 0; side < 2; side++)
        {
            int lit = m is null ? 0 : Lit(side == 0 ? m.L : m.R);
            for (int k = 0; k < Leds; k++)
            {
                var brush = k >= lit ? Off : k == Leds - 1 ? Red : k == Leds - 2 ? Yellow : Green;
                double y = h - (k + 1) * ch - k * 2;
                dc.DrawRectangle(brush, null, new Rect(side * (cw + 2), y, cw, ch));
            }
        }
    }
}
