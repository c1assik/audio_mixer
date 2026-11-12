using Microsoft.Win32;

namespace Mixer.App.Ui;

/// <summary>
/// Автозапуск (v2, «Трей и автозапуск»): значение «Mixer» в ветке Run текущего пользователя с флагом --minimized.
/// Права администратора не нужны. По умолчанию выключен; включается в помощнике или в настройках.
/// </summary>
public static class Autostart
{
    public const string ValueName = "Mixer";
    public const string MinimizedSwitch = "--minimized";

    /// <summary>Ветка в HKCU (тесты подменяют её на свою).</summary>
    public static string KeyPath { get; set; } = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Строка запуска: путь в кавычках и --minimized; --graph переносится, если схема своя.</summary>
    public static string CommandFor(string exe, string? graphPath = null) =>
        $"\"{exe}\" {MinimizedSwitch}" + (graphPath is null ? "" : $" --graph \"{graphPath}\"");

    /// <summary>Автозапуск включён именно для этой программы (путь совпадает).</summary>
    public static bool IsEnabled(string? exe = null)
    {
        exe ??= Environment.ProcessPath;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string cmd && exe is not null && cmd.Contains($"\"{exe}\"", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    /// <summary>Включить или выключить. Ошибка — исключение (UnauthorizedAccessException, SecurityException, IOException).</summary>
    public static void Set(bool on, string? exe = null, string? graphPath = null)
    {
        exe ??= Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (on) key.SetValue(ValueName, CommandFor(exe, graphPath), RegistryValueKind.String);
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
