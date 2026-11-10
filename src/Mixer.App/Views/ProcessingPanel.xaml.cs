using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Mixer.App.Ui;
using Mixer.App.ViewModels;

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
