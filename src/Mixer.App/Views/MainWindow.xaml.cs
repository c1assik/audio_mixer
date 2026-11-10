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
        SourceInitialized += (_, _) => WindowsLook.DarkTitleBar(this);
        // свёрнутое окно не рисует индикаторы: такт останавливается
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) MeterClock.Stop();
            else MeterClock.Start();
        };
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        Deactivated += (_, _) => Graph.EndSpacePan();
    }

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
            else if (!Graph.CancelInteraction()) vm.Select(null);
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
                if (!e.IsRepeat) Graph.SpaceHeld = true;
                Graph.Cursor = Cursors.ScrollAll;
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        Graph.EndSpacePan();
        Graph.Cursor = null;
        e.Handled = !TypingInTextBox;
    }

    private void OnScrimClick(object sender, MouseButtonEventArgs e)
    {
        // клик мимо диалога закрывает его
        if (e.OriginalSource == sender) Vm?.CloseOverlay();
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
