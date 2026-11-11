using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Mixer.App.Ui;

/// <summary>
/// Поле назначения горячей клавиши: щёлкнуть и нажать сочетание; Backspace или Delete — снять, Esc — оставить как было.
/// Пока поле в фокусе, свои горячие клавиши микшера отпущены (<see cref="Capturing"/>), иначе сочетание сработало бы
/// вместо назначения. Конфликт показывается сразу — красным текстом под полем и в подсказке.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    private readonly Func<KeyChord?, string?> _assign;
    private readonly Action<bool> _capturing;
    private string? _current;

    /// <param name="current">Текущее сочетание (null — не задано).</param>
    /// <param name="assign">Назначить (null — снять); возвращает причину отказа или null.</param>
    /// <param name="capturing">Поле ловит сочетание (true) или отпустило фокус (false).</param>
    public HotkeyBox(string? current, Func<KeyChord?, string?> assign, Action<bool> capturing)
    {
        _current = current;
        _assign = assign;
        _capturing = capturing;
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Width = 150;
        Cursor = Cursors.Hand;
        InputMethod.SetIsInputMethodEnabled(this, false);
        Show();
        GotKeyboardFocus += (_, _) =>
        {
            Text = Strings.Hotkey_Press;
            _capturing(true);
        };
        LostKeyboardFocus += (_, _) =>
        {
            Show();
            _capturing(false);
        };
        AutomationProperties.SetName(this, Strings.Hotkey_Menu);
    }

    /// <summary>Последняя причина отказа (для подсказки и тестов).</summary>
    public string? Error { get; private set; }

    private void Show()
    {
        Text = _current ?? Strings.Hotkey_None;
        Foreground = (Brush?)TryFindResource(_current is null ? "Muted" : "Text") ?? Brushes.Gray;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        switch (key)
        {
            case Key.Escape:
                MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                return;
            case Key.Back or Key.Delete when Keyboard.Modifiers == ModifierKeys.None:
                Commit(null);
                return;
            case Key.Tab when Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift:
                e.Handled = false; // Tab — уйти из поля
                return;
        }
        if (KeyChord.FromKey(key, Keyboard.Modifiers) is { } chord) Commit(chord);
    }

    private void Commit(KeyChord? chord)
    {
        Error = _assign(chord);
        if (Error is null)
        {
            _current = chord?.ToString();
            ToolTip = null;
            Show();
        }
        else
        {
            Text = Strings.Hotkey_Press;
            ToolTip = Error;
            if (ToolTip is not null) ToolTipService.SetInitialShowDelay(this, 0);
        }
    }
}
