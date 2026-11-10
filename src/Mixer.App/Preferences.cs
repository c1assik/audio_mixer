using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Mixer.App;

/// <summary>
/// Настройки программы, а не схемы: settings.json рядом с graph.json (%AppData%\Mixer). Пока в них только язык.
/// Испорченный или недоступный файл не мешает запуску: берутся значения по умолчанию.
/// </summary>
public sealed class Preferences
{
    /// <summary>Язык как в Windows.</summary>
    public const string Auto = "auto";

    public static readonly string[] Languages = [Auto, "en", "ru"];

    private sealed record Data(string? Language);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public Preferences(string path, CultureInfo windowsCulture)
    {
        Path = path;
        WindowsCulture = windowsCulture;
    }

    public string Path { get; }

    /// <summary>Язык интерфейса Windows (до того, как программа выставила свой).</summary>
    public CultureInfo WindowsCulture { get; }

    /// <summary>auto, en или ru.</summary>
    public string Language { get; private set; } = Auto;

    public static string PathFor(string graphPath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(graphPath)) ?? ".", "settings.json");

    public static Preferences Load(string path, CultureInfo windowsCulture, out string? error)
    {
        error = null;
        var p = new Preferences(path, windowsCulture);
        try
        {
            if (!File.Exists(path)) return p;
            var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(path), Json);
            if (d?.Language is { } lang && Languages.Contains(lang)) p.Language = lang;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
        }
        return p;
    }

    /// <summary>Сохранить язык. Ошибка записи — исключение (IOException, UnauthorizedAccessException).</summary>
    public void SetLanguage(string language)
    {
        if (!Languages.Contains(language)) throw new ArgumentOutOfRangeException(nameof(language), language, null);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Data(language), Json));
        File.Move(tmp, Path, overwrite: true);
        Language = language;
    }

    /// <summary>Культура, с которой программа запустится при этой настройке (без --lang).</summary>
    public CultureInfo Resolve(string language) => AppOptions.PickCulture(language, WindowsCulture);
}
