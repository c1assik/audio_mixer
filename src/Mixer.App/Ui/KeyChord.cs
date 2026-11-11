using System.Windows.Input;

namespace Mixer.App.Ui;

/// <summary>
/// Сочетание клавиш горячей клавиши: «Ctrl+Shift+M», «F13», «Ctrl+Alt+NumPad1». Строка — то, что хранится в graph.json
/// и показывается в интерфейсе; разбор не зависит от регистра.
/// </summary>
public readonly record struct KeyChord(ModifierKeys Modifiers, Key Key)
{
    private static readonly (ModifierKeys Mod, string Name)[] Mods =
    [
        (ModifierKeys.Control, "Ctrl"), (ModifierKeys.Alt, "Alt"), (ModifierKeys.Shift, "Shift"), (ModifierKeys.Windows, "Win"),
    ];

    /// <summary>Клавиши-модификаторы сами по себе сочетанием не считаются.</summary>
    public static bool IsModifier(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin or Key.System or Key.None;

    /// <summary>Сочетание из нажатия в поле назначения. null — нажат только модификатор.</summary>
    public static KeyChord? FromKey(Key key, ModifierKeys mods) => IsModifier(key) ? null : new KeyChord(mods, key);

    public static KeyChord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var mods = ModifierKeys.None;
        Key? key = null;
        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Mods.FirstOrDefault(x => x.Name.Equals(raw, StringComparison.OrdinalIgnoreCase) || raw.Equals("Control", StringComparison.OrdinalIgnoreCase) && x.Mod == ModifierKeys.Control);
            if (m.Name is not null)
            {
                mods |= m.Mod;
                continue;
            }
            if (key is not null) return null; // две обычные клавиши
            string name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
            if (!Enum.TryParse(name, ignoreCase: true, out Key k) || IsModifier(k)) return null;
            key = k;
        }
        return key is { } kk ? new KeyChord(mods, kk) : null;
    }

    public override string ToString()
    {
        var mods = Modifiers;
        var parts = Mods.Where(m => mods.HasFlag(m.Mod)).Select(m => m.Name).ToList();
        parts.Add(Key is >= Key.D0 and <= Key.D9 ? ((int)(Key - Key.D0)).ToString() : Key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>Виртуальный код клавиши Windows.</summary>
    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    /// <summary>Модификаторы для RegisterHotKey (MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4, MOD_WIN 8).</summary>
    public uint NativeModifiers =>
        (Modifiers.HasFlag(ModifierKeys.Alt) ? 1u : 0) | (Modifiers.HasFlag(ModifierKeys.Control) ? 2u : 0)
        | (Modifiers.HasFlag(ModifierKeys.Shift) ? 4u : 0) | (Modifiers.HasFlag(ModifierKeys.Windows) ? 8u : 0);

    /// <summary>Нормализованная запись или null, если строка не разбирается.</summary>
    public static string? Normalize(string? text) => Parse(text)?.ToString();
}
