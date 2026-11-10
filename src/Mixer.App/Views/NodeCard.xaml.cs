using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mixer.App.ViewModels;
using Mixer.App.Ui;
using DspDb = Mixer.Audio.Dsp.Db;

namespace Mixer.App.Views;

public partial class NodeCard : UserControl
{
    public NodeCard()
    {
        InitializeComponent();
        MouseEnter += (_, _) => Main?.SetHover(Vm);
        MouseLeave += (_, _) =>
        {
            if (Main?.Interacting == false) Main.SetHover(null);
        };
    }

    private NodeViewModel? Vm => DataContext as NodeViewModel;
    private MainViewModel? Main => Window.GetWindow(this)?.DataContext as MainViewModel;

    // ── имя: двойной клик или F2 ──

    private void OnNameDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || Vm is null) return;
        Vm.BeginRenameCommand.Execute(null);
        e.Handled = true;
    }

    private void OnNameBoxVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!NameBox.IsVisible || Vm is null) return;
        NameBox.Text = Vm.Name;
        Dispatcher.BeginInvoke(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        });
    }

    private void OnNameBoxKey(object sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        if (e.Key == Key.Enter)
        {
            Vm.CommitRename(NameBox.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Vm.IsRenaming = false;
            e.Handled = true;
        }
    }

    private void OnNameBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Vm?.IsRenaming == true) Vm.CommitRename(NameBox.Text);
    }

    // ── меню ──

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || Main is null) return;
        var menu = NodeMenus.ForNode(Main, Vm);
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ── фейдер: клик ставит значение и сразу тянет, двойной клик — 0 дБ, колесо — шаг 1 дБ ──

    private bool _faderDrag;

    private double ValueAt(MouseEventArgs e)
    {
        double w = Math.Max(1, Fader.ActualWidth - 14);
        double f = Math.Clamp((e.GetPosition(Fader).X - 7) / w, 0, 1);
        return DspDb.Min + f * (DspDb.Max - DspDb.Min);
    }

    private void OnFaderDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null) return;
        e.Handled = true;
        Fader.Focus();
        if (e.ClickCount == 2)
        {
            Vm.ResetGainCommand.Execute(null);
            return;
        }
        _faderDrag = true;
        Fader.CaptureMouse();
        Vm.GainDb = Fmt.SnapDb(ValueAt(e));
    }

    private void OnFaderMove(object sender, MouseEventArgs e)
    {
        if (!_faderDrag || Vm is null) return;
        Vm.GainDb = Fmt.SnapDb(ValueAt(e), snapZero: !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    private void OnFaderUp(object sender, MouseButtonEventArgs e)
    {
        if (!_faderDrag) return;
        Fader.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnFaderLost(object sender, MouseEventArgs e)
    {
        if (!_faderDrag) return;
        _faderDrag = false;
        Main?.EndMerge();
    }

    private void OnFaderWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm is null) return;
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 1;
        double from = Vm.GainDb <= DspDb.Min && e.Delta > 0 ? DspDb.Min : Vm.GainDb;
        Vm.GainDb = Fmt.SnapDb(from + Math.Sign(e.Delta) * step, snapZero: false);
        e.Handled = true;
    }
}
