using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mixer.App.Ui;
using Mixer.App.ViewModels;
using Mixer.Core.Editing;

namespace Mixer.App.Views;

/// <summary>Холст окна: карточки (<see cref="GraphView"/>) или стойка (<see cref="RackView"/>); окну нужны только эти жесты.</summary>
public interface ICanvasView
{
    bool SpaceHeld { get; set; }
    void EndSpacePan();
    bool CancelInteraction();
    Cursor? Cursor { get; set; }
}

/// <summary>
/// Стойка темы Modular (v2, «Темы»): модули рядами, гнёзда, патч-кабели с провисом поверх панелей. Раскладка
/// вычисляется (<see cref="RackLayout"/>), а не хранится; модели те же, что у карточек, и все правки идут теми же
/// командами <see cref="MainViewModel"/>. Жесты: от OUT к IN — новый кабель; штекер из IN — перецепить или выдернуть;
/// со свободного IN — к источнику; модуль перетаскивается среди соседей по колонке; наведение подсвечивает маршруты, а
/// кабели, пока мышь над модулем, полупрозрачны.
/// </summary>
public partial class RackView : UserControl, ICanvasView
{
    private enum Mode { None, Connect, Reroute, ModulePending, ModuleMove, Pan }

    private MainViewModel? _vm;
    private RackLayoutResult _layout = RackLayout.Compute(new Mixer.Core.Config.GraphConfig(), 1000);
    private readonly Dictionary<string, ModuleView> _views = new();
    private string _layoutKey = "";
    private bool _rebuildQueued;
    private Mode _mode;
    private Point _down;
    private NodeViewModel? _origin;
    private bool _fromInput;
    private WireViewModel? _wire;
    private NodeViewModel? _moving;
    private Point _panStart;
    private double _panH, _panV;
    private bool _suppressKnob;

    public bool SpaceHeld { get; set; }

    public RackView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as MainViewModel);
        Scroller.SizeChanged += (_, _) => QueueRebuild();
        Surface.MouseLeftButtonDown += OnLeftDown;
        Surface.MouseMove += OnMove;
        Surface.MouseLeftButtonUp += OnLeftUp;
        Surface.MouseRightButtonUp += OnRightUp;
        Surface.MouseDown += OnOtherDown;
        Surface.MouseUp += OnOtherUp;
        Surface.MouseLeave += (_, _) =>
        {
            if (_mode == Mode.None) Hover(null);
        };
        Surface.LostMouseCapture += (_, _) =>
        {
            if (_mode != Mode.None) Cancel();
        };
        Scroller.PreviewMouseWheel += OnWheel;
    }

    private void Attach(MainViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.Nodes.CollectionChanged -= OnCollectionChanged;
            _vm.Wires.CollectionChanged -= OnCollectionChanged;
            _vm.WiresInvalidated -= OnWiresInvalidated;
            _vm.PropertyChanged -= OnVmChanged;
        }
        _vm = vm;
        Cables.Attach(this, vm);
        if (vm is null) return;
        vm.Nodes.CollectionChanged += OnCollectionChanged;
        vm.Wires.CollectionChanged += OnCollectionChanged;
        vm.WiresInvalidated += OnWiresInvalidated;
        vm.PropertyChanged += OnVmChanged;
        QueueRebuild();
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Zoom)) QueueRebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRebuild();

    private void OnWiresInvalidated()
    {
        QueueRebuild();
        Cables.InvalidateVisual();
    }

    internal RackLayoutResult Layout => _layout;

    // ── раскладка ──

    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _rebuildQueued = false;
            Rebuild();
        });
    }

    /// <summary>Пересчитать стойку, если сменились узлы, провода, порядок или ширина окна (уровни и hover её не трогают).</summary>
    private void Rebuild(bool force = false)
    {
        if (_vm is null) return;
        var g = _vm.Graph;
        double width = Math.Max(600, (Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : ActualWidth) / Math.Max(0.5, _vm.Zoom) - 8);
        string key = string.Join(",", RackLayout.Order(g).Select(n => n.Id)) + "|" + string.Join(",", g.Wires.Select(w => w.From + ">" + w.To))
                     + "|" + Math.Round(width / 20);
        if (!force && key == _layoutKey) return;
        _layoutKey = key;
        _layout = RackLayout.Compute(g, width);
        Surface.Width = _layout.Width;
        Surface.Height = _layout.Height;
        Rails.Layout = _layout;

        foreach (string gone in _views.Keys.Where(id => !_layout.Modules.ContainsKey(id)).ToList())
        {
            Modules.Children.Remove(_views[gone]);
            _views.Remove(gone);
        }
        foreach (var m in _layout.Modules.Values)
        {
            if (_vm.FindNode(m.NodeId) is not { } n) continue;
            if (!_views.TryGetValue(m.NodeId, out var view))
            {
                view = new ModuleView { DataContext = n };
                _views[m.NodeId] = view;
                Modules.Children.Add(view);
            }
            view.Width = m.Width;
            view.Height = m.Height;
            Canvas.SetLeft(view, m.X);
            Canvas.SetTop(view, m.Y);
        }
        BuildOverlay();
        Cables.InvalidateVisual();
    }

    /// <summary>Гнёзда, подписи IN k / OUT и маленькие ручки проводов над гнёздами IN.</summary>
    private void BuildOverlay()
    {
        Overlay.Children.Clear();
        var vm = _vm!;
        foreach (var m in _layout.Modules.Values)
        {
            var (_, ink, _) = ModuleView.ColorsFor(m.Column);
            var inkBrush = TryFindResource(ink) as Brush ?? Brushes.Black;
            foreach (var j in m.Inputs.Append(m.Output).OfType<RackJack>())
            {
                var jack = new JackElement(j) { ToolTip = j.Input ? Strings.Modular_InTip : Strings.Modular_OutTip };
                Canvas.SetLeft(jack, j.X - JackElement.Size / 2);
                Canvas.SetTop(jack, j.Y - JackElement.Size / 2);
                Overlay.Children.Add(jack);
                var label = new Border
                {
                    Background = j.Input ? Brushes.Transparent : inkBrush,
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(4, 0, 4, 0),
                    IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        Text = j.Input ? $"IN {j.Index + 1}" : "OUT",
                        FontFamily = TryFindResource("PanelFont") as FontFamily,
                        FontSize = 9.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = j.Input ? inkBrush : (TryFindResource(m.Column == Mixer.Core.Config.NodeColumn.Buses ? "PanelBlack" : "PanelAlu") as Brush ?? Brushes.White),
                    },
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, j.X - label.DesiredSize.Width / 2);
                Canvas.SetTop(label, j.Y + JackElement.Size / 2 + 1);
                Overlay.Children.Add(label);

                // маленькая ручка над гнездом IN — уровень провода, который в него воткнут
                if (j.Input && j.From is { } from && vm.FindWire(from, j.NodeId) is { } w)
                {
                    var knob = new Knob { Small = true, Width = 22, Height = 22, Value = w.GainDb, ToolTip = Strings.Modular_CableKnobTip, Foreground = inkBrush };
                    System.Windows.Automation.AutomationProperties.SetName(knob, Fmt.F(Strings.Wire_TooltipTitleFormat, w.Source.Name, w.Target.Name));
                    knob.ValueChanged += (_, e) =>
                    {
                        if (!_suppressKnob) vm.SetWireGain(w, e.NewValue, snapZero: false);
                    };
                    knob.DragCompleted += vm.EndMerge;
                    knob.ResetRequested += () => vm.ResetWire(w);
                    var value = new TextBlock { FontSize = 9, Foreground = TryFindResource(ModuleView.ColorsFor(m.Column).Muted) as Brush, IsHitTestVisible = false };
                    void Show()
                    {
                        value.Text = w.PillText;
                        value.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        Canvas.SetLeft(value, j.X - value.DesiredSize.Width / 2);
                    }
                    w.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName is not (nameof(WireViewModel.GainDb) or nameof(WireViewModel.Enabled))) return;
                        _suppressKnob = true;
                        knob.Value = w.GainDb;
                        _suppressKnob = false;
                        Show();
                    };
                    Show();
                    Canvas.SetLeft(knob, j.X - 11);
                    Canvas.SetTop(knob, j.Y - 60);
                    Canvas.SetTop(value, j.Y - 36);
                    Overlay.Children.Add(knob);
                    Overlay.Children.Add(value);
                }
            }
        }
    }

    // ── мышь ──

    private ModuleView? ModuleAt(object? source)
    {
        for (var d = source as DependencyObject; d is not null && d != Surface; d = VisualTreeHelper.GetParent(d))
            if (d is ModuleView mv) return mv;
        return null;
    }

    /// <summary>Модуль под точкой стойки с нужным портом (с запасом под гнёздами).</summary>
    private NodeViewModel? TargetAt(Point p, bool wantInput)
    {
        foreach (var m in _layout.Modules.Values)
        {
            var r = new Rect(m.X - 6, m.Y - 6, m.Width + 12, m.Height + 30);
            if (!r.Contains(p) || _vm!.FindNode(m.NodeId) is not { } n || n == _origin) continue;
            if (wantInput ? n.HasInPort : n.HasOutPort) return n;
        }
        return null;
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;
        var p = e.GetPosition(Surface);
        Focus();
        if (SpaceHeld)
        {
            StartPan(e);
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is JackElement jack && _vm.FindNode(jack.Jack.NodeId) is { } node)
        {
            if (jack.Jack.Input && jack.Jack.From is { } from && _vm.FindWire(from, node.Id) is { } w)
            {
                // выдернуть штекер: перецепить в другой модуль или бросить в пустоту
                _wire = w;
                _wire.IsDetached = true;
                _origin = w.Source;
                _fromInput = false;
                _mode = Mode.Reroute;
                _vm.Select(w);
                _vm.BeginConnectPreview(_origin, false, w);
            }
            else
            {
                _origin = node;
                _fromInput = jack.Jack.Input;
                _mode = Mode.Connect;
                _vm.BeginConnectPreview(node, _fromInput);
            }
            _down = p;
            Surface.CaptureMouse();
            Cables.Ghost = (JackPoint(_origin!, _fromInput), p, _origin!.WireColor, false);
            e.Handled = true;
            return;
        }
        if (ModuleAt(e.OriginalSource) is { DataContext: NodeViewModel mn })
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _vm.TogglePick(mn);
            else
            {
                _vm.Select(mn);
                _moving = mn;
                _down = p;
                _mode = Mode.ModulePending;
                Surface.CaptureMouse();
            }
            e.Handled = true;
            return;
        }
        var hit = Cables.Hit(p);
        _vm.Select(hit);
        e.Handled = true;
    }

    /// <summary>Гнездо узла: OUT у источника и шины; у шины и выхода при подключении «от входа» — свободное IN.</summary>
    private Point JackPoint(NodeViewModel n, bool input)
    {
        if (!_layout.Modules.TryGetValue(n.Id, out var m)) return new Point();
        var j = input ? m.Inputs.LastOrDefault() : m.Output;
        return j is null ? new Point(m.X + m.Width / 2, m.Y + m.Height) : new Point(j.X, j.Y);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_vm is null) return;
        var p = e.GetPosition(Surface);
        switch (_mode)
        {
            case Mode.None:
                Hover(ModuleAt(e.OriginalSource)?.DataContext as NodeViewModel);
                break;
            case Mode.Connect or Mode.Reroute:
                UpdateConnect(p);
                break;
            case Mode.ModulePending when (p - _down).Length > 6:
                _mode = Mode.ModuleMove;
                Surface.Cursor = Cursors.SizeWE;
                goto case Mode.ModuleMove;
            case Mode.ModuleMove:
                if (_moving is not null && RackLayout.MoveTarget(_layout, _moving.Id, p.X, p.Y) is { } other && _vm.FindNode(other) is { } o)
                {
                    // перестановка среди соседей по колонке: модули меняются местами (их Y), Graphite остаётся как был
                    double y1 = _moving.Config.Y, y2 = o.Config.Y;
                    if (Math.Abs(y1 - y2) < 0.5) y2 = y1 + (string.CompareOrdinal(o.Id, _moving.Id) < 0 ? -1 : 1);
                    _vm.Edit(g => GraphEdits.UpdateNode(GraphEdits.UpdateNode(g, _moving.Id, c => c with { Y = y2 }), o.Id, c => c with { Y = y1 }), "rack-move");
                }
                break;
            case Mode.Pan:
                var s = e.GetPosition(Scroller);
                Scroller.ScrollToHorizontalOffset(_panH - (s.X - _panStart.X));
                Scroller.ScrollToVerticalOffset(_panV - (s.Y - _panStart.Y));
                break;
        }
    }

    private void UpdateConnect(Point p)
    {
        var target = TargetAt(p, !_fromInput);
        var look = target is null ? PortLook.None : _fromInput ? target.OutPort : target.InPort;
        bool valid = look == PortLook.Valid;
        var a = JackPoint(_origin!, _fromInput);
        var end = target is not null ? JackPoint(target, !_fromInput) : p;
        Cables.Ghost = _fromInput ? (end, a, target?.WireColor ?? _origin!.WireColor, valid) : (a, end, _origin!.WireColor, valid);
        if (look is PortLook.Forbidden or PortLook.Occupied && target?.PortReason is { } why)
        {
            ReasonText.Text = why;
            var at = Surface.TranslatePoint(p, this);
            Canvas.SetLeft(ReasonTip, at.X + 14);
            Canvas.SetTop(ReasonTip, at.Y + 16);
            ReasonTip.Visibility = Visibility.Visible;
        }
        else ReasonTip.Visibility = Visibility.Collapsed;
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;
        var p = e.GetPosition(Surface);
        var mode = _mode;
        _mode = Mode.None;
        switch (mode)
        {
            case Mode.Connect when TargetAt(p, !_fromInput) is { } t:
                if (_fromInput) _vm.Connect(t.Id, _origin!.Id);
                else _vm.Connect(_origin!.Id, t.Id);
                break;
            case Mode.Reroute when _wire is { } w:
                if (TargetAt(p, true) is { } to) _vm.Reroute(w, w.From, to.Id);
                else if ((p - _down).Length > 8)
                {
                    _vm.DeleteWire(w); // выдернули штекер в пустоту
                    _vm.Notify(Fmt.F(Strings.Graph_WireDeletedFormat, w.Source.Name, w.Target.Name));
                }
                break;
            case Mode.ModuleMove:
                _vm.EndMerge();
                break;
        }
        Cleanup();
        Surface.ReleaseMouseCapture();
    }

    private void Cleanup()
    {
        if (_origin is not null || _wire is { IsDetached: true }) _vm?.EndConnectPreview();
        if (_wire is not null) _wire.IsDetached = false;
        _wire = null;
        _origin = null;
        _moving = null;
        Cables.Ghost = null;
        ReasonTip.Visibility = Visibility.Collapsed;
        Surface.Cursor = null;
    }

    private void Cancel()
    {
        _mode = Mode.None;
        Cleanup();
        _vm?.EndMerge();
        Surface.ReleaseMouseCapture();
    }

    public bool CancelInteraction()
    {
        if (_mode == Mode.None) return false;
        Cancel();
        return true;
    }

    private void Hover(NodeViewModel? n)
    {
        if (Cables.HoverNode == n) return;
        Cables.HoverNode = n;
        _vm?.SetHover(n); // подсветка маршрутов: в Modular обязательна
        Cables.InvalidateVisual();
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || _mode != Mode.None) return;
        var p = e.GetPosition(Surface);
        ContextMenu? menu = null;
        if (ModuleAt(e.OriginalSource) is { DataContext: NodeViewModel n })
        {
            if (!n.IsSelected || _vm.Picked.Count < 2) _vm.Select(n);
            menu = NodeMenus.ForNode(_vm, n);
        }
        else if (e.OriginalSource is JackElement j && j.Jack.From is { } from && _vm.FindWire(from, j.Jack.NodeId) is { } jw)
        {
            _vm.Select(jw);
            menu = NodeMenus.ForWire(_vm, jw);
        }
        else if (Cables.Hit(p) is { } w)
        {
            _vm.Select(w);
            menu = NodeMenus.ForWire(_vm, w);
        }
        else _vm.OpenPalette(PaletteMode.Sources, null);
        if (menu is not null)
        {
            menu.PlacementTarget = Surface;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }
        e.Handled = true;
    }

    private void OnOtherDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _mode != Mode.None) return;
        StartPan(e);
        e.Handled = true;
    }

    private void OnOtherUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _mode != Mode.Pan) return;
        _mode = Mode.None;
        Surface.ReleaseMouseCapture();
        Surface.Cursor = null;
        e.Handled = true;
    }

    private void StartPan(MouseEventArgs e)
    {
        _mode = Mode.Pan;
        _panStart = e.GetPosition(Scroller);
        _panH = Scroller.HorizontalOffset;
        _panV = Scroller.VerticalOffset;
        Surface.Cursor = Cursors.ScrollAll;
        Surface.CaptureMouse();
    }

    public void EndSpacePan()
    {
        SpaceHeld = false;
        if (_mode == Mode.Pan) Cancel();
        Surface.Cursor = null;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm is null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        _vm.Zoom = Math.Clamp(Math.Round(_vm.Zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), 2), 0.5, 2.0);
        e.Handled = true;
    }
}

/// <summary>Гнездо 3,5 мм: ободок и отверстие; под ним подпись IN k / OUT (её ставит <see cref="RackView"/>).</summary>
public sealed class JackElement : FrameworkElement
{
    public const double Size = 24;

    public JackElement(RackJack jack)
    {
        Jack = jack;
        Width = Size;
        Height = Size;
        Cursor = Cursors.Cross;
        System.Windows.Automation.AutomationProperties.SetName(this, jack.Input ? $"IN {jack.Index + 1}" : "OUT");
    }

    public RackJack Jack { get; }

    protected override void OnRender(DrawingContext dc)
    {
        var c = new Point(Size / 2, Size / 2);
        dc.DrawEllipse(TryFindResource("Screw") as Brush ?? Brushes.Gray, new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), 1), c, Size / 2 - 1, Size / 2 - 1);
        dc.DrawEllipse(TryFindResource("JackHole") as Brush ?? Brushes.Black, null, c, Size / 2 - 6, Size / 2 - 6);
    }

    protected override HitTestResult HitTestCore(PointHitTestParameters p) => new PointHitTestResult(this, p.HitPoint);
}

/// <summary>Рейки стойки: сверху и снизу каждого ряда модулей.</summary>
public sealed class RackRails : FrameworkElement
{
    private RackLayoutResult? _layout;

    public RackLayoutResult? Layout
    {
        get => _layout;
        set
        {
            _layout = value;
            InvalidateVisual();
        }
    }

    public RackRails() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        if (_layout is null) return;
        var rail = TryFindResource("Rail") as Brush ?? Brushes.DimGray;
        var hole = TryFindResource("JackHole") as Brush ?? Brushes.Black;
        foreach (var row in _layout.Modules.Values.GroupBy(m => m.Row))
        {
            double y = row.First().Y, right = row.Max(m => m.X + m.Width) + 12, left = row.Min(m => m.X) - 12;
            foreach (double ry in new[] { y - 10, y + RackLayout.ModuleHeight - 2 })
            {
                dc.DrawRectangle(rail, null, new Rect(left, ry, right - left, 12));
                for (double hx = left + 10; hx < right - 6; hx += 22) dc.DrawEllipse(hole, null, new Point(hx, ry + 6), 1.8, 1.8);
            }
        }
    }
}

/// <summary>
/// Патч-кабели поверх модулей: провис 50 + 0,22 × длина, тень, штекеры на концах; из одного OUT несколько кабелей —
/// стопкой штекеров. Пока мышь над модулем, чужие кабели почти прозрачны, его — яркие (подсветка маршрутов).
/// </summary>
public sealed class CableLayer : FrameworkElement
{
    private RackView? _rack;
    private MainViewModel? _vm;
    private (Point A, Point B, Color Color, bool Valid)? _ghost;

    public NodeViewModel? HoverNode { get; set; }

    public (Point A, Point B, Color Color, bool Valid)? Ghost
    {
        get => _ghost;
        set
        {
            _ghost = value;
            InvalidateVisual();
        }
    }

    public CableLayer()
    {
        Loaded += (_, _) => MeterClock.Render += OnFrame;
        Unloaded += (_, _) => MeterClock.Render -= OnFrame;
    }

    internal void Attach(RackView rack, MainViewModel? vm)
    {
        _rack = rack;
        _vm = vm;
        InvalidateVisual();
    }

    private readonly Dictionary<WireViewModel, int> _levels = new();

    private void OnFrame()
    {
        if (_vm is null) return;
        foreach (var w in _vm.Wires)
            if (!_levels.TryGetValue(w, out int seen) || seen != (int)Math.Round(w.Meter.Level * 10))
            {
                InvalidateVisual();
                return;
            }
    }

    /// <summary>Штекер k в стопке у OUT: каждый следующий выше на 9 px.</summary>
    private static Point PlugAt(RackCable c) => new(c.AX, c.AY - c.Plug * 9);

    /// <summary>Кабель под точкой (ближе 8 px) — по той же кривой, что рисуется.</summary>
    public WireViewModel? Hit(Point p)
    {
        if (_rack is null || _vm is null) return null;
        WireViewModel? best = null;
        double bestD = 8;
        bool patch = WireGeometry.Patch;
        WireGeometry.Patch = true;
        try
        {
            foreach (var c in _rack.Layout.Cables)
            {
                var (d, _) = WireGeometry.Nearest(PlugAt(c), new Point(c.BX, c.BY), p);
                if (d < bestD && _vm.FindWire(c.From, c.To) is { } w)
                {
                    best = w;
                    bestD = d;
                }
            }
        }
        finally
        {
            WireGeometry.Patch = patch;
        }
        return best;
    }

    private static readonly Brush Shadow = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0));
    private static readonly Brush PlugBody = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));

    protected override void OnRender(DrawingContext dc)
    {
        _levels.Clear();
        if (_rack is null || _vm is null) return;
        bool patch = WireGeometry.Patch;
        WireGeometry.Patch = true;
        try
        {
            var hover = HoverNode;
            foreach (var c in _rack.Layout.Cables)
            {
                if (_vm.FindWire(c.From, c.To) is not { } w) continue;
                _levels[w] = (int)Math.Round(w.Meter.Level * 10);
                if (w.IsDetached) continue;
                var a = PlugAt(c);
                var b = new Point(c.BX, c.BY);
                var geo = WireGeometry.Build(a, b);
                Color color = w.Forbidden is not null ? ThemeColors.Get("Red", Colors.Red) : !w.Enabled ? ThemeColors.Get("WireOff", Colors.Gray) : w.Source.WireColor;
                double opacity = hover is null ? 1 : w.Source == hover || w.Target == hover ? 1 : 0.18;
                if (w.Source.Muted || w.Target.Muted) opacity *= 0.6;
                double thick = 4.5 + 1.2 * w.Meter.Level;
                dc.PushOpacity(opacity);
                dc.PushTransform(new TranslateTransform(2, 4));
                dc.DrawGeometry(null, new Pen(Shadow, thick + 1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
                dc.Pop();
                if (w.IsSelected)
                    dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x70, color.R, color.G, color.B)), thick + 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
                var pen = new Pen(new SolidColorBrush(color), thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                if (!w.Enabled || w.Forbidden is not null) pen.DashStyle = new DashStyle([1.6, 1.4], 0);
                dc.DrawGeometry(null, pen, geo);
                Plug(dc, a, color);
                Plug(dc, b, color);
                dc.Pop();
            }
            if (_ghost is { } g)
            {
                var geo = WireGeometry.Build(g.A, g.B);
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(g.Color), 4.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, DashStyle = g.Valid ? null : new DashStyle([1.6, 1.4], 0) }, geo);
                Plug(dc, g.A, g.Color);
                Plug(dc, g.B, g.Color);
            }
        }
        finally
        {
            WireGeometry.Patch = patch;
        }
    }

    /// <summary>Штекер: цветное кольцо и тёмный корпус в гнезде.</summary>
    private static void Plug(DrawingContext dc, Point p, Color color)
    {
        dc.DrawEllipse(new SolidColorBrush(color), new Pen(PlugBody, 1.5), p, 9.5, 9.5);
        dc.DrawEllipse(PlugBody, null, p, 4.5, 4.5);
    }
}
