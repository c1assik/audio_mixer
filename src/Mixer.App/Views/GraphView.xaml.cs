using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Mixer.App.Ui;
using Mixer.App.ViewModels;
using Mixer.Core.Editing;

namespace Mixer.App.Views;

/// <summary>
/// Холст: мышь и колесо. Вся логика графа — в <see cref="MainViewModel"/>, здесь только жесты:
/// перетаскивание узла в своей колонке, провод от порта к порту (в обе стороны), перецепка или удаление провода
/// за конец, пилюля (колесо, клик, двойной клик, протяжка), панорама средней кнопкой или пробелом, Ctrl + колесо — масштаб.
/// </summary>
public partial class GraphView : UserControl, ICanvasView
{
    private enum Mode { None, NodePending, NodeMove, Connect, WirePending, Reroute, Pan, PillPending, PillDrag }

    private Mode _mode;
    private Point _down;
    private NodeViewModel? _node;
    private Vector _grab;
    private NodeViewModel? _origin;
    /// <summary>Провод тянут от входного порта (звук пойдёт в _origin), иначе — от выходного.</summary>
    private bool _fromInput;
    private WireViewModel? _wire;
    private bool _moveTargetEnd;
    private double _pillStartGain;
    private bool _pillDoubleClicked;
    private Point _panStart;
    private double _panH, _panV;
    private MainViewModel? _vm;

    private readonly ToolTip _tip = new() { Placement = PlacementMode.Mouse, HorizontalOffset = 14, VerticalOffset = 14 };
    private readonly DispatcherTimer _tipTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _mergeTimer = new() { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime() + 50) };
    private WireViewModel? _hoverWire;

    /// <summary>Пробел зажат: левая кнопка двигает холст (ставит окно).</summary>
    public bool SpaceHeld { get; set; }

    public GraphView()
    {
        InitializeComponent();
        OutputsLabel.Margin = new Thickness(GraphLayout.OutputX, 26, 0, 0);
        BusesLabel.Margin = new Thickness(GraphLayout.BusX, 26, 0, 0);
        _tip.ContentTemplate = (DataTemplate)Resources["WireTip"];
        _tip.PlacementTarget = Wires;
        _tipTimer.Tick += (_, _) =>
        {
            _tipTimer.Stop();
            if (_hoverWire is null || _mode != Mode.None) return;
            _tip.Content = _hoverWire;
            _tip.IsOpen = true;
        };
        _mergeTimer.Tick += (_, _) =>
        {
            _mergeTimer.Stop();
            _vm?.EndMerge();
        };

        Surface.MouseLeftButtonDown += OnLeftDown;
        Surface.MouseLeftButtonUp += OnLeftUp;
        Surface.MouseMove += OnMove;
        Surface.MouseRightButtonUp += OnRightUp;
        Surface.MouseDown += OnOtherDown;
        Surface.MouseUp += OnOtherUp;
        Surface.MouseLeave += (_, _) => SetHoverWire(null);
        Surface.LostMouseCapture += (_, _) =>
        {
            if (_mode != Mode.None) Cancel();
        };
        Scroller.PreviewMouseWheel += OnWheel;
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.DeviceMenuRequested -= OnDeviceMenu;
            _vm = e.NewValue as MainViewModel;
            if (_vm is not null) _vm.DeviceMenuRequested += OnDeviceMenu;
        };
    }

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private static (string? Role, FrameworkElement? Element) FindRole(object? source, DependencyObject stop)
    {
        var d = source as DependencyObject;
        while (d is not null && d != stop)
        {
            if (d is FrameworkElement fe && Ui.Ui.GetRole(fe) is { } role) return (role, fe);
            d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return (null, null);
    }

    private void Capture()
    {
        Surface.CaptureMouse();
        SetHoverWire(null);
    }

    // ── левая кнопка ──

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
        var (role, el) = FindRole(e.OriginalSource, Surface);
        switch (role)
        {
            case "port-in" or "port-out" when el!.DataContext is NodeViewModel node:
                _mode = Mode.Connect;
                _origin = node;
                _fromInput = role == "port-in";
                Capture();
                _vm.BeginConnectPreview(node, _fromInput);
                UpdateConnect(p);
                break;

            case "card" when el!.DataContext is NodeViewModel node && Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                // Ctrl + клик: несколько узлов, чтобы сгруппировать в шину
                _vm.TogglePick(node);
                break;

            case "pill" when el!.DataContext is WireViewModel w:
                _vm.Select(w);
                _wire = w;
                _down = p;
                _pillStartGain = w.GainDb;
                _pillDoubleClicked = e.ClickCount >= 2;
                if (_pillDoubleClicked) _vm.ResetWire(w);
                _mode = Mode.PillPending;
                Capture();
                break;

            case "card" when el!.DataContext is NodeViewModel node:
                _vm.Select(node);
                _node = node;
                _down = p;
                _grab = p - new Point(node.X, node.Y);
                _mode = Mode.NodePending;
                Capture();
                break;

            default:
                var (hit, t) = Wires.Hit(p);
                _vm.Select(hit);
                if (hit is not null)
                {
                    _wire = hit;
                    _down = p;
                    _moveTargetEnd = t >= 0.5;
                    // тянуть можно за конец: у выхода — перецепить выход, у источника — источник
                    _mode = t is <= 0.3 or >= 0.7 ? Mode.WirePending : Mode.None;
                    if (_mode != Mode.None) Capture();
                }
                break;
        }
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_vm is null) return;
        var p = e.GetPosition(Surface);
        switch (_mode)
        {
            case Mode.None:
                if (e.LeftButton == MouseButtonState.Released && e.OriginalSource == Wires) SetHoverWire(Wires.Hit(p).Wire);
                else if (e.OriginalSource != Wires) SetHoverWire(null);
                break;

            case Mode.NodePending when (p - _down).Length > 3:
                _mode = Mode.NodeMove;
                goto case Mode.NodeMove;
            case Mode.NodeMove:
                _vm.MoveNode(_node!, p.X - _grab.X, p.Y - _grab.Y);
                break;

            case Mode.Connect:
                UpdateConnect(p);
                break;

            case Mode.WirePending when (p - _down).Length > 4:
                _mode = Mode.Reroute;
                _wire!.IsDetached = true;
                _origin = _moveTargetEnd ? _wire.Source : _wire.Target;
                _fromInput = !_moveTargetEnd;
                _vm.BeginConnectPreview(_origin, _fromInput, _wire);
                goto case Mode.Reroute;
            case Mode.Reroute:
                UpdateConnect(p);
                break;

            case Mode.Pan:
                var s = e.GetPosition(Scroller);
                Scroller.ScrollToHorizontalOffset(_panH - (s.X - _panStart.X));
                Scroller.ScrollToVerticalOffset(_panV - (s.Y - _panStart.Y));
                break;

            case Mode.PillPending when Math.Abs(p.Y - _down.Y) > 3 && !_pillDoubleClicked:
                _mode = Mode.PillDrag;
                goto case Mode.PillDrag;
            case Mode.PillDrag:
                bool fine = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                _vm.SetWireGain(_wire!, _pillStartGain + (_down.Y - p.Y) * (fine ? 0.02 : 0.2), snapZero: !fine);
                break;
        }
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;
        var p = e.GetPosition(Surface);
        var mode = _mode;
        _mode = Mode.None;
        switch (mode)
        {
            case Mode.NodeMove:
            case Mode.PillDrag:
                _vm.EndMerge();
                break;
            case Mode.Connect:
                FinishConnect(p);
                break;
            case Mode.Reroute:
                FinishReroute(p);
                break;
            case Mode.PillPending when !_pillDoubleClicked:
                _vm.ToggleWire(_wire!);
                // клик и двойной клик — один шаг истории; после окна двойного клика — отдельные шаги
                _mergeTimer.Stop();
                _mergeTimer.Start();
                break;
        }
        CleanupDrag();
        Surface.ReleaseMouseCapture();
    }

    private void Cancel()
    {
        _mode = Mode.None;
        CleanupDrag();
        _vm?.EndMerge();
        Surface.ReleaseMouseCapture();
    }

    /// <summary>Esc: бросить то, что тянется.</summary>
    public bool CancelInteraction()
    {
        if (_mode == Mode.None) return false;
        Cancel();
        return true;
    }

    private void CleanupDrag()
    {
        if (_origin is not null || _wire is { IsDetached: true })
        {
            if (_wire is not null) _wire.IsDetached = false;
            _vm?.EndConnectPreview();
        }
        _origin = null;
        _node = null;
        Wires.ClearGhost();
        ReasonTip.Visibility = Visibility.Collapsed;
        if (_hoverWire is null) Surface.Cursor = null;
    }

    // ── провод ──

    /// <summary>Узел с нужным портом под точкой: по карточке с запасом или рядом с портом.</summary>
    /// <param name="wantInput">Нужен входной порт (звук пойдёт в узел), иначе выходной.</param>
    private NodeViewModel? TargetAt(Point p, bool wantInput)
    {
        NodeViewModel? best = null;
        double bestD = double.MaxValue;
        foreach (var n in _vm!.Nodes)
        {
            if (n == _origin || (wantInput ? !n.HasInPort : !n.HasOutPort)) continue;
            var r = n.Bounds;
            r.Inflate(18, 14);
            double d = (n.PortFor(wantInput) - p).Length;
            if (!r.Contains(p) && d > 32) continue;
            if (d < bestD)
            {
                best = n;
                bestD = d;
            }
        }
        return best;
    }

    private void UpdateConnect(Point p)
    {
        var origin = _origin!;
        var target = TargetAt(p, !_fromInput);
        var look = target is null ? PortLook.None : _fromInput ? target.OutPort : target.InPort;
        bool valid = look == PortLook.Valid;
        if (!_fromInput) Wires.SetGhost(origin.OutPoint, target?.InPoint ?? p, origin.WireColor, valid);
        else Wires.SetGhost(target?.OutPoint ?? p, origin.InPoint, target?.WireColor ?? origin.WireColor, valid);

        if (look is PortLook.Forbidden or PortLook.Occupied && target?.PortReason is { } why)
        {
            ReasonText.Text = why;
            Canvas.SetLeft(ReasonTip, p.X + 14);
            Canvas.SetTop(ReasonTip, p.Y + 16);
            ReasonTip.Visibility = Visibility.Visible;
        }
        else
        {
            ReasonTip.Visibility = Visibility.Collapsed;
        }
    }

    private void FinishConnect(Point p)
    {
        var origin = _origin;
        if (origin is null) return;
        var target = TargetAt(p, !_fromInput);
        if (target is null) return;
        if (_fromInput) _vm!.Connect(target.Id, origin.Id);
        else _vm!.Connect(origin.Id, target.Id);
    }

    private void FinishReroute(Point p)
    {
        var w = _wire;
        var fixedEnd = _origin;
        if (w is null || fixedEnd is null) return;
        var target = TargetAt(p, !_fromInput);
        if (target is null)
        {
            _vm!.DeleteWire(w); // оттянули конец в пустоту
            _vm.Notify(Fmt.F(Strings.Graph_WireDeletedFormat, w.Source.Name, w.Target.Name));
            return;
        }
        if (_moveTargetEnd) _vm!.Reroute(w, w.From, target.Id);
        else _vm!.Reroute(w, target.Id, w.To);
    }

    // ── правая и средняя кнопки ──

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || _mode != Mode.None) return;
        var p = e.GetPosition(Surface);
        var (role, el) = FindRole(e.OriginalSource, Surface);
        ContextMenu? menu = null;
        if (role is "card" or "port-in" or "port-out" && el!.DataContext is NodeViewModel node)
        {
            // правый клик по одному из выделенных узлов не сбрасывает выделение: меню предложит сгруппировать
            if (!node.IsSelected || _vm.Picked.Count < 2) _vm.Select(node);
            menu = NodeMenus.ForNode(_vm, node);
        }
        else if (role == "pill" && el!.DataContext is WireViewModel pw)
        {
            _vm.Select(pw);
            menu = NodeMenus.ForWire(_vm, pw);
        }
        else if (role is null && Wires.Hit(p).Wire is { } w)
        {
            _vm.Select(w);
            menu = NodeMenus.ForWire(_vm, w);
        }
        else if (role is null)
        {
            switch (GraphLayout.ColumnAt(p.X))
            {
                case Mixer.Core.Config.NodeColumn.Buses:
                    _vm.AddBus(p.Y);
                    break;
                case Mixer.Core.Config.NodeColumn.Sources:
                    _vm.OpenPalette(PaletteMode.Sources, p.Y);
                    break;
                default:
                    _vm.OpenPalette(PaletteMode.Outputs, p.Y);
                    break;
            }
        }
        if (menu is not null)
        {
            SetHoverWire(null);
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
        Capture();
    }

    public void EndSpacePan()
    {
        SpaceHeld = false;
        if (_mode == Mode.Pan) Cancel();
        Surface.Cursor = null;
    }

    // ── колесо ──

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm is null) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var at = e.GetPosition(Surface);
            var m = e.GetPosition(Scroller);
            double z = Math.Clamp(Math.Round(_vm.Zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), 2), 0.5, 2.0);
            _vm.Zoom = z;
            Scroller.UpdateLayout();
            var after = Surface.TranslatePoint(at, Scroller);
            Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + after.X - m.X);
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + after.Y - m.Y);
            e.Handled = true;
            return;
        }
        var (role, el) = FindRole(e.OriginalSource, Surface);
        if (role == "pill" && el!.DataContext is WireViewModel w)
        {
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 1;
            _vm.SetWireGain(w, w.GainDb + Math.Sign(e.Delta) * step, snapZero: false);
            e.Handled = true;
        }
    }

    // ── подсказка провода ──

    private void SetHoverWire(WireViewModel? w)
    {
        if (_hoverWire == w) return;
        if (_hoverWire is not null) _hoverWire.IsHovered = false;
        _hoverWire = w;
        _tip.IsOpen = false;
        _tipTimer.Stop();
        if (w is not null)
        {
            w.IsHovered = true;
            _tipTimer.Start();
        }
        _vm?.InvalidateWires();
        Surface.Cursor = w is null ? null : Cursors.Hand;
    }

    private void OnDeviceMenu(NodeViewModel n)
    {
        if (_vm is null) return;
        var menu = NodeMenus.DeviceMenu(_vm, n);
        menu.PlacementTarget = Surface;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }
}
