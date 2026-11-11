using System.Windows;
using System.Windows.Media;

namespace Mixer.App.Ui;

/// <summary>
/// Темы (v2, «Темы»): у каждой свой словарь с одинаковым набором ключей (Themes/Graphite.xaml, Daylight.xaml,
/// Modular.xaml), стили общие (Themes/Styles.xaml). App.OnStartup подключает словарь до создания окна; тема меняется
/// после перезапуска, как язык: все ссылки в XAML — StaticResource.
/// </summary>
public static class ThemeManager
{
    public const string Graphite = "graphite";
    public const string Daylight = "daylight";
    public const string Modular = "modular";

    public static readonly string[] All = [Graphite, Daylight, Modular];

    /// <summary>Имя файла словаря темы.</summary>
    public static string FileOf(string theme) => theme switch
    {
        Daylight => "Daylight",
        Modular => "Modular",
        _ => "Graphite",
    };

    public static Uri DictionaryUri(string theme) => new($"pack://application:,,,/Mixer;component/Themes/{FileOf(theme)}.xaml", UriKind.Absolute);

    public static readonly Uri StylesUri = new("pack://application:,,,/Mixer;component/Themes/Styles.xaml", UriKind.Absolute);

    /// <summary>Подключить тему (неизвестная — Graphite). Возвращает примененную.</summary>
    public static string Apply(Application app, string? theme)
    {
        string t = All.Contains(theme) ? theme! : Graphite;
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DictionaryUri(t) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = StylesUri });
        return t;
    }
}

/// <summary>
/// Цвета темы для кода, который рисует сам (индикаторы, кривая EQ, провода, значок трея). Без загруженной темы
/// (тесты) — цвета Graphite.
/// </summary>
public static class ThemeColors
{
    /// <summary>Палитра источников Graphite: так цвет узла хранится в graph.json, тема показывает свой оттенок того же места.</summary>
    public static readonly string[] Canonical = ["#4FA3FF", "#FF9A4D", "#3DCC91", "#B18CFF", "#F2C94C", "#FF6B9A", "#4DD0E1", "#A3D977"];

    public static Color Get(string key, Color fallback) =>
        Application.Current?.TryFindResource(key + "Color") is Color c ? c
        : Application.Current?.TryFindResource(key) is SolidColorBrush b ? b.Color
        : fallback;

    public static Brush Brush(string key, Color fallback)
    {
        var b = new SolidColorBrush(Get(key, fallback));
        b.Freeze();
        return b;
    }

    /// <summary>Цвет источника в текущей теме: цвет из палитры — место в палитре темы, свой цвет — как есть.</summary>
    public static Color Source(Color stored)
    {
        for (int i = 0; i < Canonical.Length; i++)
        {
            var c = (Color)ColorConverter.ConvertFromString(Canonical[i]);
            if (c == stored) return Get("Source" + i, c);
        }
        return stored;
    }
}
