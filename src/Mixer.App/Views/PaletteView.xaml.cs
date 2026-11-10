using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mixer.App.ViewModels;

namespace Mixer.App.Views;

public partial class PaletteView : UserControl
{
    public PaletteView()
    {
        InitializeComponent();
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => Search.Focus());
    }

    private PaletteViewModel? Vm => DataContext as PaletteViewModel;

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        switch (e.Key)
        {
            case Key.Down:
                Vm.Move(+1);
                break;
            case Key.Up:
                Vm.Move(-1);
                break;
            case Key.Enter:
                Vm.Commit();
                break;
            case Key.Escape:
                OnClose(this, e);
                break;
            default:
                return;
        }
        if (Vm.SelectedIndex >= 0) List.ScrollIntoView(List.Items[Vm.SelectedIndex]);
        e.Handled = true;
    }

    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: PaletteRow row }) Vm?.Commit(row);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is MainViewModel main) main.CloseOverlay();
    }
}
