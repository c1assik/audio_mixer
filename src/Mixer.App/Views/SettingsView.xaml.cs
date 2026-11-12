using System.Windows.Controls;
using System.Windows.Threading;
using Mixer.App.ViewModels;

namespace Mixer.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is not SettingsViewModel { StartAtAbout: true }) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Scroll.ScrollToEnd());
            // список устройств приходит позже и сдвигает «О программе» вниз
            Scroll.ScrollChanged += (_, e) => { if (e.ExtentHeightChange != 0) Scroll.ScrollToEnd(); };
        };
    }
}
