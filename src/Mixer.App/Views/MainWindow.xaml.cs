using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Mixer.App.Ui;
using Mixer.App.ViewModels;

namespace Mixer.App.Views;

public partial class MainWindow : Window
{
    public static readonly IValueConverter NotNullToVisible = new NotNullConverter();

    public MainWindow()
    {
        InitializeComponent();
        // тема Modular: вместо холста с карточками — стойка с модулями (те же модели и команды)
        Canvas = Graph;
        if (App.Theme == ThemeManager.Modular && Graph.Parent is Grid host)
        {
            var rack = new RackView { Focusable = true, FocusVisualStyle = null };
            Grid.SetRow(rack, Grid.GetRow(Graph));
            Grid.SetColumn(rack, Grid.GetColumn(Graph));
            int at = host.Children.IndexOf(Graph);
            host.Children.Remove(Graph);
            host.Children.Insert(at, rack);
            Canvas = rack;
        }
        // тёмный заголовок — у тёмных тем; у Daylight обычный светлый
        SourceInitialized += (_, _) =>
        {
            if (TryFindResource("IsDarkTheme") is not false) WindowsLook.DarkTitleBar(this);
        };
        // свёрнутое или спрятанное в трей окно не рисует индикаторы: такт останавливается
        StateChanged += (_, _) => UpdateClock();
        IsVisibleChanged += (_, _) => UpdateClock();
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        Deactivated += (_, _) => Canvas.EndSpacePan();
    }

    private void UpdateClock()
    {
        if (WindowState == WindowState.Minimized || !IsVisible) MeterClock.Stop();
        else MeterClock.Start();
    }

    /// <summary>Холст окна: карточки или стойка (тема Modular).</summary>
    public ICanvasView Canvas { get; }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private static bool TypingInTextBox => Keyboard.FocusedElement is TextBox;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            if (vm.HasOverlay) vm.CloseOverlay();
            else if (!Canvas.CancelInteraction()) vm.Select(null);
            e.Handled = true;
            return;
        }
        if (vm.HasOverlay || TypingInTextBox) return;

        bool ctrl = mods.HasFlag(ModifierKeys.Control);
        switch (key)
        {
            case Key.Z when ctrl && mods.HasFlag(ModifierKeys.Shift):
            case Key.Y when ctrl:
                vm.RedoCommand.Execute(null);
                break;
            case Key.Z when ctrl:
                vm.UndoCommand.Execute(null);
                break;
            case Key.Delete or Key.Back:
                vm.DeleteSelectedCommand.Execute(null);
                break;
            case Key.F2:
                vm.RenameSelectedCommand.Execute(null);
                break;
            case Key.OemPlus or Key.Add when ctrl:
                vm.ZoomInCommand.Execute(null);
                break;
            case Key.OemMinus or Key.Subtract when ctrl:
                vm.ZoomOutCommand.Execute(null);
                break;
            case Key.D0 or Key.NumPad0 when ctrl:
                vm.ZoomResetCommand.Execute(null);
                break;
            case Key.Space:
                // пробел + перетаскивание — прокрутка холста; кнопку под фокусом пробел не нажимает
                if (!e.IsRepeat) Canvas.SpaceHeld = true;
                Canvas.Cursor = Cursors.ScrollAll;
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        Canvas.EndSpacePan();
        Canvas.Cursor = null;
        e.Handled = !TypingInTextBox;
    }

    private void OnScrimClick(object sender, MouseButtonEventArgs e)
    {
        // клик мимо диалога закрывает его
        if (e.OriginalSource == sender) Vm?.CloseOverlay();
    }

    private void OnSceneMenu(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var menu = NodeMenus.ForScenes(Vm);
        menu.PlacementTarget = SceneButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnToastClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not null) Vm.Toast = null;
    }

    private sealed class NotNullConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
