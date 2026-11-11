using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mixer.App.Ui;

/// <summary>
/// Подсказка для надписи с многоточием (TextTrimming): если текст не влез, при наведении он показывается целиком.
/// Extra — своя подсказка надписи (у карточки — exe, план «Весь звук системы»…): она показывается всегда, а полный
/// текст, если обрезан, — над ней. Текст влез и своей подсказки нет — подсказка не появляется.
/// </summary>
public static class TrimTip
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(TrimTip), new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty ExtraProperty = DependencyProperty.RegisterAttached(
        "Extra", typeof(string), typeof(TrimTip), new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);
    public static string? GetExtra(DependencyObject d) => (string?)d.GetValue(ExtraProperty);
    public static void SetExtra(DependencyObject d, string? value) => d.SetValue(ExtraProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.ToolTipOpening -= OnOpening;
        if (e.NewValue is not true) return;
        // ToolTipOpening приходит, только если подсказка задана: содержимое собирается в момент открытия
        tb.ToolTip = new ToolTip();
        tb.ToolTipOpening += OnOpening;
    }

    private static void OnOpening(object sender, ToolTipEventArgs e)
    {
        var tb = (TextBlock)sender;
        string? text = Compose(tb.Text, IsTrimmed(tb), GetExtra(tb));
        if (text is null || tb.ToolTip is not ToolTip tip)
        {
            e.Handled = true; // показывать нечего
            return;
        }
        tip.Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 };
    }

    /// <summary>Текст подсказки: полный текст, если обрезан, затем своя подсказка; null — подсказки нет.</summary>
    public static string? Compose(string? text, bool trimmed, string? extra)
    {
        bool hasExtra = !string.IsNullOrWhiteSpace(extra) && extra != text;
        if (trimmed && !string.IsNullOrEmpty(text)) return hasExtra ? text + "\n\n" + extra : text;
        return hasExtra ? extra : null;
    }

    /// <summary>Не влезает ли однострочный текст в ширину надписи.</summary>
    public static bool IsTrimmed(TextBlock tb)
    {
        if (string.IsNullOrEmpty(tb.Text) || tb.ActualWidth <= 0) return false;
        var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
        var ft = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, tb.FlowDirection, typeface, tb.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(tb).PixelsPerDip);
        double available = tb.ActualWidth - tb.Padding.Left - tb.Padding.Right;
        return ft.WidthIncludingTrailingWhitespace > available + 0.5;
    }
}
