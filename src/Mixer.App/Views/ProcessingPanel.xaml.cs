using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Mixer.App.Ui;
using Mixer.App.ViewModels;
using Mixer.Core.Config;
using Mixer.Core.Editing;

namespace Mixer.App.Views;

/// <summary>Боковая панель обработки: жесты (колесо над значением, двойной клик — по умолчанию), меню пресетов и типов.</summary>
public partial class ProcessingPanel : UserControl
{
    private ProcessingViewModel? _vm;

    public ProcessingPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = e.NewValue as ProcessingViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmChanged;
                ScrollTo(_vm.FocusBlock);
            }
            SaveRow.Visibility = Visibility.Collapsed;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProcessingViewModel.FocusBlock)) ScrollTo(_vm?.FocusBlock);
    }

    private void ScrollTo(string? block)
    {
        FrameworkElement? target = block switch
        {
            "fx" => PreCard,
            "gate" => GateCard,
            "eq" => EqCard,
            "comp" => CompCard,
            _ => null,
        };
        if (target is null) return;
        Dispatcher.BeginInvoke(() => target.BringIntoView(new Rect(0, 0, target.ActualWidth, target.ActualHeight)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Параметр под курсором: строка ползунка или ячейка таблицы EQ (Tag = ParamViewModel).</summary>
    private static ParamViewModel? ParamAt(object? source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is FrameworkElement { Tag: ParamViewModel p }) return p;
            if (d is ProcessingPanel) return null;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        // над кривой колесо меняет добротность (это решает сама кривая); над значением — значение
        if (e.OriginalSource is EqCurve) return;
        if (ParamAt(e.OriginalSource) is not { } p) return;
        p.Nudge(Math.Sign(e.Delta) * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 1));
        _vm?.EndEdit();
        e.Handled = true;
    }

    private void OnSliderUp(object sender, MouseButtonEventArgs e) => _vm?.EndEdit();

    private void OnParamReset(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ParamViewModel p) return;
        p.Reset();
        _vm?.EndEdit();
        e.Handled = true;
    }

    // ── пресеты ──

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var menu = new ContextMenu();
        bool user = false;
        foreach (var p in _vm.Presets)
        {
            if (!p.BuiltIn && !user)
            {
                menu.Items.Add(new Separator());
                user = true;
            }
            var mi = new MenuItem { Header = p.Name, IsCheckable = true, IsChecked = p.Name == _vm.PresetName };
            var preset = p;
            mi.Click += (_, _) => _vm.ApplyPreset(preset);
            menu.Items.Add(mi);
        }
        menu.PlacementTarget = PresetButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        bool show = SaveRow.Visibility != Visibility.Visible;
        SaveRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        PresetNameBox.Text = _vm?.Node.Name ?? "";
        Dispatcher.BeginInvoke(() =>
        {
            PresetNameBox.Focus();
            PresetNameBox.SelectAll();
        });
    }

    private void OnSavePreset(object sender, RoutedEventArgs e) => SavePreset();

    private void OnPresetNameKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SavePreset();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SaveRow.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void SavePreset()
    {
        if (_vm?.SavePreset(PresetNameBox.Text) == true) SaveRow.Visibility = Visibility.Collapsed;
    }

    // ── слоты плагинов: перетаскивание за ручку и меню «⋯» ──

    private Point _dragFrom;
    private PluginSlotViewModel? _dragSlot;

    private void OnHandleDown(object sender, MouseButtonEventArgs e)
    {
        _dragSlot = (sender as FrameworkElement)?.DataContext as PluginSlotViewModel;
        _dragFrom = e.GetPosition(this);
    }

    private void OnHandleMove(object sender, MouseEventArgs e)
    {
        if (_dragSlot is null || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(this) - _dragFrom).Length < 4) return;
        var slot = _dragSlot;
        _dragSlot = null;
        DragDrop.DoDragDrop((DependencyObject)sender, slot, DragDropEffects.Move);
    }

    /// <summary>Бросили на слот — встать на его место; на секцию — в конец её группы.</summary>
    private void OnSlotDrop(object sender, DragEventArgs e)
    {
        if (_vm is null || e.Data.GetData(typeof(PluginSlotViewModel)) is not PluginSlotViewModel slot) return;
        e.Handled = true;
        var (group, index) = (sender as FrameworkElement)?.Tag switch
        {
            PluginSlotViewModel t when _vm.PreSlots.Contains(t) => (PluginGroup.Pre, _vm.PreSlots.IndexOf(t)),
            PluginSlotViewModel t => (PluginGroup.Post, _vm.PostSlots.IndexOf(t)),
            "post" => (PluginGroup.Post, int.MaxValue),
            _ => (PluginGroup.Pre, int.MaxValue),
        };
        if (slot == (sender as FrameworkElement)?.Tag) return;
        if (group != slot.Config.Group && PluginEdits.Group(_vm.Node.Config, group).Count >= PluginEdits.MaxPerGroup)
        {
            _vm.Owner.Notify(Strings.Plugin_NoRoom, error: true);
            return;
        }
        _vm.Owner.MovePlugin(_vm.Node, slot.Id, group, index);
    }

    private void OnSlotMenu(object sender, RoutedEventArgs e)
    {
        if (_vm is null || (sender as FrameworkElement)?.DataContext is not PluginSlotViewModel slot) return;
        var main = _vm.Owner;
        var node = _vm.Node;
        var p = slot.Config;
        var other = p.Group == PluginGroup.Pre ? PluginGroup.Post : PluginGroup.Pre;
        var menu = new ContextMenu();
        MenuItem Item(string header, Action click, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => click();
            return mi;
        }
        menu.Items.Add(Item(Strings.Plugin_Replace, () => main.OpenPluginPicker(new PluginTarget(node, p.Group, p.Id))));
        menu.Items.Add(Item(other == PluginGroup.Post ? Strings.Plugin_MoveToPost : Strings.Plugin_MoveToPre,
            () => main.MovePlugin(node, p.Id, other, int.MaxValue), PluginEdits.Group(node.Config, other).Count < PluginEdits.MaxPerGroup));
        menu.Items.Add(new Separator());

        // «Сохранить пресет»: имя вводится прямо в меню, Enter — сохранить
        var save = new MenuItem { Header = Strings.Plugin_SavePreset };
        var box = new TextBox { MinWidth = 180, Text = p.Name };
        box.KeyDown += (_, k) =>
        {
            if (k.Key != Key.Enter) return;
            menu.IsOpen = false;
            _ = main.SavePluginPreset(node, p, box.Text);
        };
        save.Items.Add(new MenuItem { Header = box, StaysOpenOnClick = true });
        save.SubmenuOpened += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            box.SelectAll();
        });
        menu.Items.Add(save);
        var load = new MenuItem { Header = Strings.Plugin_LoadPreset };
        foreach (string name in main.PluginPresets(p)) load.Items.Add(Item(name, () => _ = main.LoadPluginPreset(node, p, name)));
        load.IsEnabled = load.Items.Count > 0;
        menu.Items.Add(load);
        menu.Items.Add(new Separator());
        var even = new MenuItem { Header = Strings.Plugin_EvenBlocks, IsCheckable = true, IsChecked = p.EvenBlocks, ToolTip = Strings.Plugin_EvenBlocksTip };
        even.Click += (_, _) => main.SetEvenBlocks(node, p.Id, even.IsChecked);
        menu.Items.Add(even);
        menu.Items.Add(Item(Strings.Plugin_Remove, () => main.RemovePlugin(node, p.Id)));
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ── типы полос ──

    private void OnBandType(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EqBandViewModel band) return;
        var menu = EqCurve.BandTypeMenu(band);
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnHighPassType(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.HighPassOn = !_vm.HighPassOn;
    }
}
