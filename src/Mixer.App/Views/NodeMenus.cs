using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Mixer.App.Ui;
using Mixer.App.ViewModels;
using Mixer.Audio.Interop;
using Mixer.Core.Config;
using Mixer.Core.Editing;
using Mixer.Core.Graph;

namespace Mixer.App.Views;

/// <summary>Меню узла («⋯» и правый клик) и провода.</summary>
public static class NodeMenus
{
    private static MenuItem Item(string header, Action click, string? gesture = null, bool enabled = true, string? tip = null)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
        mi.Click += (_, _) => click();
        if (tip is not null)
        {
            mi.ToolTip = tip;
            ToolTipService.SetShowOnDisabled(mi, true);
        }
        return mi;
    }

    private static MenuItem Check(string header, bool on, Action click, string? tip = null)
    {
        var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = on };
        mi.Click += (_, _) => click();
        if (tip is not null) mi.ToolTip = tip;
        return mi;
    }

    public static ContextMenu ForNode(MainViewModel main, NodeViewModel n)
    {
        var menu = new ContextMenu();
        var picked = main.Picked;
        if (picked.Count >= 2 && picked.Contains(n))
        {
            menu.Items.Add(Item(Fmt.F(Strings.Bus_GroupMenuFormat, picked.Count), () => main.GroupSelectedCommand.Execute(null)));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item(Strings.Node_Rename, () => n.BeginRenameCommand.Execute(null), "F2"));

        // «Вывести в…» / «Принимать из…» — чекбоксы для тех, кто не хочет тянуть провод; у шины есть оба
        if (n.HasOutPort) menu.Items.Add(Routes(main, n, outgoing: true));
        if (n.HasInPort) menu.Items.Add(Routes(main, n, outgoing: false));

        menu.Items.Add(Item(Strings.Fx_Menu, () => main.OpenProcessing(n)));
        if (n.IsSource)
        {
            menu.Items.Add(Check(Strings.Node_Mono, n.Mono, () => n.Mono = !n.Mono, Strings.Node_MonoTip));
            menu.Items.Add(BalanceItem(main, n));
        }
        if (n.IsApp)
        {
            menu.Items.Add(Check(Strings.Node_TakeOver, n.TakeOver, () => n.TakeOver = !n.TakeOver, Strings.Node_TakeOverTip));
            var direct = Check(Strings.Node_DirectRoute, n.DirectRoute, () => n.DirectRoute = !n.DirectRoute, Strings.Node_DirectRouteTip);
            direct.IsEnabled = n.TakeOver; // прямой маршрут — разновидность «Забрать себе»: приложение переназначает микшер
            ToolTipService.SetShowOnDisabled(direct, true);
            menu.Items.Add(direct);
        }
        if (n.IsSystem)
        {
            menu.Items.Add(ExcludeItem(main, n));
            menu.Items.Add(Check(Strings.Node_IncludeOwnNodes, n.IncludeOwnNodes, () => n.IncludeOwnNodes = !n.IncludeOwnNodes, Strings.Node_IncludeOwnNodesTip));
        }
        if (n.SupportsLowLatency)
        {
            // на входах режим экспериментальный: у части драйверов поток записи с малым периодом сбоит
            bool input = !n.IsOutput;
            menu.Items.Add(Check(input ? Strings.Node_LowLatencyInput : Strings.Node_LowLatency, n.LowLatency, () => n.LowLatency = !n.LowLatency,
                input ? Strings.Node_LowLatencyTip + "\n\n" + Strings.Node_LowLatencyInputNote : Strings.Node_LowLatencyTip));
        }
        if (n.HasDevice) menu.Items.Add(DeviceItem(main, n));
        if (n.IsSource) menu.Items.Add(ColorItem(main, n));
        if (n.IsBus) menu.Items.Add(Item(Strings.Bus_Ungroup, () => main.Ungroup(n), tip: Strings.Bus_UngroupTip));

        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Strings.Node_ResetGain, () => n.ResetGainCommand.Execute(null)));
        menu.Items.Add(Item(Strings.Node_Delete, () => main.DeleteNode(n), "Del"));
        return menu;
    }

    private static MenuItem Routes(MainViewModel main, NodeViewModel n, bool outgoing)
    {
        var routes = new MenuItem { Header = outgoing ? Strings.Node_RouteTo : Strings.Node_ReceiveFrom };
        var others = main.Nodes.Where(o => o != n && (outgoing ? o.HasInPort : o.HasOutPort))
            .OrderBy(o => o.Column).ThenBy(o => o.Y);
        foreach (var other in others)
        {
            var (src, dst) = outgoing ? (n, other) : (other, n);
            bool on = main.FindWire(src.Id, dst.Id) is not null;
            var check = main.CheckConnect(src.Id, dst.Id);
            bool allowed = on || check.Ok;
            var mi = new MenuItem { Header = other.Name, IsCheckable = true, IsChecked = on, IsEnabled = allowed, StaysOpenOnClick = true };
            mi.Click += (_, _) => main.ToggleConnection(src, dst);
            if (!allowed)
            {
                mi.ToolTip = check.Reason;
                ToolTipService.SetShowOnDisabled(mi, true);
            }
            routes.Items.Add(mi);
        }
        if (routes.Items.Count == 0)
            routes.Items.Add(new MenuItem { Header = outgoing ? Strings.Node_AddOutputFirst : Strings.Node_AddSourceFirst, IsEnabled = false });
        return routes;
    }

    /// <summary>
    /// «Исключить приложения»: исключённые (галочка, клик возвращает), затем играющие сейчас (клик исключает) и поле
    /// для любого exe — чтобы убрать приложение до того, как оно зазвучит.
    /// </summary>
    private static MenuItem ExcludeItem(MainViewModel main, NodeViewModel n)
    {
        var item = new MenuItem { Header = Strings.Node_Exclude, ToolTip = Strings.Node_ExcludeTip };
        var excluded = n.Exclude.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string exe in n.Exclude)
        {
            string e = exe;
            var mi = new MenuItem { Header = e, IsCheckable = true, IsChecked = true };
            mi.Click += (_, _) => main.SetExcluded(n, e, false);
            item.Items.Add(mi);
        }
        if (item.Items.Count > 0) item.Items.Add(new Separator());

        var own = main.Nodes.Where(x => x.IsApp && x.Config.Exe is { Length: > 0 }).Select(x => Mixer.Core.Sys.ProcessSnapshot.NormalizeExe(x.Config.Exe!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var playing = main.PlayingApps.Where(e => !excluded.Contains(e) && (n.IncludeOwnNodes || !own.Contains(e))).ToList();
        item.Items.Add(new MenuItem { Header = Strings.Node_ExcludeNowPlaying, IsEnabled = false });
        foreach (string exe in playing)
        {
            string e = exe;
            var mi = new MenuItem { Header = e, IsCheckable = true, IsChecked = false };
            mi.Click += (_, _) => main.SetExcluded(n, e, true);
            item.Items.Add(mi);
        }
        if (playing.Count == 0) item.Items.Add(new MenuItem { Header = Strings.Node_ExcludeNothing, IsEnabled = false });
        item.Items.Add(new Separator());

        var box = new TextBox { Width = 150, Margin = new Thickness(6, 0, 0, 0), ToolTip = Strings.Node_ExcludeOtherTip };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter || string.IsNullOrWhiteSpace(box.Text)) return;
            main.SetExcluded(n, box.Text, true);
            e.Handled = true;
            for (DependencyObject? p = item; p is not null; p = LogicalTreeHelper.GetParent(p))
                if (p is ContextMenu cm)
                {
                    cm.IsOpen = false;
                    break;
                }
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = Strings.Node_ExcludeOther, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(box);
        item.Items.Add(new MenuItem { Header = row, StaysOpenOnClick = true, ToolTip = Strings.Node_ExcludeOtherTip });
        return item;
    }

    private static MenuItem BalanceItem(MainViewModel main, NodeViewModel n)
    {
        var slider = new Slider
        {
            Style = (Style)Application.Current.Resources["Fader"],
            Minimum = -1,
            Maximum = 1,
            Value = n.Balance,
            Width = 150,
            Foreground = n.ColorBrush,
            IsMoveToPointEnabled = true,
            SmallChange = 0.05,
            LargeChange = 0.25,
        };
        var label = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, Foreground = (Brush)Application.Current.Resources["Muted"], FontSize = 12 };
        void Show() => label.Text = Math.Abs(slider.Value) < 0.02 ? Strings.Node_BalanceCenter
            : Fmt.F(slider.Value < 0 ? Strings.Node_BalanceLeftFormat : Strings.Node_BalanceRightFormat, Fmt.Percent(Math.Abs(slider.Value) * 100));
        Show();
        slider.ValueChanged += (_, e) =>
        {
            double v = Math.Abs(e.NewValue) < 0.04 ? 0 : Math.Round(e.NewValue, 2);
            n.Balance = v;
            Show();
        };
        slider.MouseDoubleClick += (_, _) => slider.Value = 0;
        slider.PreviewMouseLeftButtonUp += (_, _) => main.EndMerge();
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = Strings.Node_Balance, MinWidth = 62, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(slider);
        panel.Children.Add(label);
        return new MenuItem { Header = panel, StaysOpenOnClick = true, ToolTip = Strings.Node_BalanceTip };
    }

    public static MenuItem DeviceItem(MainViewModel main, NodeViewModel n)
    {
        var item = new MenuItem { Header = Strings.Node_ChooseOtherDevice };
        foreach (var mi in DeviceItems(main, n)) item.Items.Add(mi);
        return item;
    }

    public static ContextMenu DeviceMenu(MainViewModel main, NodeViewModel n)
    {
        var menu = new ContextMenu();
        foreach (var mi in DeviceItems(main, n)) menu.Items.Add(mi);
        return menu;
    }

    private static List<Control> DeviceItems(MainViewModel main, NodeViewModel n)
    {
        var flow = n.Kind == NodeKind.DeviceInput ? EDataFlow.Capture : EDataFlow.Render;
        var current = GraphBuilder.Resolve(n.Config, main.Devices);
        var list = new List<Control>();
        foreach (var role in new[] { DefaultRole.Console, DefaultRole.Communications })
        {
            var now = main.Devices.FirstOrDefault(d => d.Flow == flow && (role == DefaultRole.Communications ? d.IsDefaultComms : d.IsDefault));
            bool on = n.Config.DefaultRole == role;
            var mi = new MenuItem
            {
                Header = now is null
                    ? Fmt.F(Strings.Node_DefaultMenuNoneFormat, role == DefaultRole.Communications ? Strings.Node_DefaultCommsMenu : Strings.Node_DefaultMenu)
                    : Fmt.F(Strings.Node_DefaultMenuNowFormat, role == DefaultRole.Communications ? Strings.Node_DefaultCommsMenu : Strings.Node_DefaultMenu, Fmt.ShortDevice(now.Name)),
                IsCheckable = true,
                IsChecked = on,
                ToolTip = Strings.Node_FollowsTip,
            };
            mi.Click += (_, _) =>
            {
                if (!on) main.FollowDefault(n, role);
            };
            list.Add(mi);
        }
        list.Add(new Separator());
        int fixedCount = list.Count;
        foreach (var d in main.Devices.Where(d => d.Flow == flow).OrderBy(d => d.Name))
        {
            bool on = n.Config.DefaultRole is null && current?.Id == d.Id;
            var mi = new MenuItem { Header = d.Name, IsCheckable = true, IsChecked = on };
            mi.Click += (_, _) =>
            {
                if (!on) main.ChangeDevice(n, d);
            };
            list.Add(mi);
        }
        if (list.Count == fixedCount) list.Add(new MenuItem { Header = Strings.Node_NoDevices, IsEnabled = false });
        return list;
    }

    private static MenuItem ColorItem(MainViewModel main, NodeViewModel n)
    {
        var item = new MenuItem { Header = Strings.Node_Color };
        var row = new WrapPanel { Width = 176 };
        foreach (string hex in MainViewModel.SourceColors)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var swatch = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(4),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = hex,
                Template = SwatchTemplate(c, n.Color == c),
            };
            swatch.Click += (_, _) =>
            {
                main.SetColor(n, hex);
                if (item.Parent is ContextMenu cm) cm.IsOpen = false;
            };
            row.Children.Add(swatch);
        }
        item.Items.Add(new MenuItem { Header = row, StaysOpenOnClick = true });
        return item;
    }

    private static ControlTemplate SwatchTemplate(Color c, bool current)
    {
        var t = new ControlTemplate(typeof(Button));
        var e = new FrameworkElementFactory(typeof(Ellipse));
        e.SetValue(Shape.FillProperty, new SolidColorBrush(c));
        e.SetValue(Shape.StrokeProperty, current ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
        e.SetValue(Shape.StrokeThicknessProperty, current ? 2.0 : 1.0);
        t.VisualTree = e;
        return t;
    }

    public static ContextMenu ForWire(MainViewModel main, WireViewModel w)
    {
        var menu = new ContextMenu();
        if (w.Forbidden is null)
        {
            menu.Items.Add(Item(w.Enabled ? Strings.Wire_TurnOff : Strings.Wire_TurnOn, () => main.ToggleWire(w)));
            menu.Items.Add(Item(Strings.Wire_ResetGain, () => main.ResetWire(w)));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item(Strings.Wire_Delete, () => main.DeleteWire(w), "Del"));
        return menu;
    }
}
