using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mixer.App.Ui;

/// <summary>
/// Короткое подтверждение горячей клавиши у трея («Микрофон: выключен»): окно без рамки в правом нижнем углу рабочей
/// области, поверх всех, не забирает фокус (WS_EX_NOACTIVATE) — игра не теряет клавиатуру. Исчезает через 1,2 с.
/// </summary>
public sealed class TrayToast
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20;

    private Window? _window;
    private TextBlock? _text;
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromMilliseconds(1200) };

    public TrayToast() => _hide.Tick += (_, _) =>
    {
        _hide.Stop();
        _window?.Hide();
    };

    public void Show(string text)
    {
        if (_window is null) Create();
        _text!.Text = text;
        _window!.UpdateLayout();
        var area = SystemParameters.WorkArea;
        _window.Show();
        _window.Left = area.Right - _window.ActualWidth - 12;
        _window.Top = area.Bottom - _window.ActualHeight - 12;
        _hide.Stop();
        _hide.Start();
    }

    private void Create()
    {
        _text = new TextBlock { FontSize = 13, Foreground = Res("Text", Brushes.White), FontFamily = (FontFamily?)Application.Current?.TryFindResource("UiFont") ?? new FontFamily("Segoe UI") };
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Focusable = false,
            Content = new Border
            {
                Background = Res("Raised2", new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x34))),
                BorderBrush = Res("LineStrong", Brushes.Gray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10),
                Child = _text,
            },
        };
        _window.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(_window).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
        };
    }

    private static Brush Res(string key, Brush fallback) => Application.Current?.TryFindResource(key) as Brush ?? fallback;

    public void Close()
    {
        _hide.Stop();
        _window?.Close();
        _window = null;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
