using System.Globalization;
using System.IO;
using System.Text.Json;
using Mixer.Audio.Dsp;

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

    private sealed record Data(string? Language, List<StoredPreset>? Presets = null);

    /// <summary>Свой пресет цепочки обработки.</summary>
    public sealed record StoredPreset(string Name, ChainSettings Chain);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

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

    /// <summary>Свои пресеты цепочки обработки (по имени).</summary>
    public IReadOnlyList<StoredPreset> Presets { get; private set; } = [];

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
            if (d?.Presets is { } presets)
                p.Presets = presets.Where(x => !string.IsNullOrWhiteSpace(x.Name) && x.Chain is not null)
                    .Select(x => x with { Chain = x.Chain.Normalized() }).ToList();
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
        Write(language, Presets);
        Language = language;
    }

    /// <summary>Сохранить свой пресет (то же имя — заменить). Ошибка записи — исключение.</summary>
    public void SavePreset(string name, ChainSettings chain)
    {
        var list = Presets.Where(x => !string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase)).ToList();
        list.Add(new StoredPreset(name, chain));
        Write(Language, list);
        Presets = list;
    }

    private void Write(string language, IReadOnlyList<StoredPreset> presets)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Data(language, presets.Count == 0 ? null : presets.ToList()), Json));
        File.Move(tmp, Path, overwrite: true);
    }

    /// <summary>Культура, с которой программа запустится при этой настройке (без --lang).</summary>
    public CultureInfo Resolve(string language) => AppOptions.PickCulture(language, WindowsCulture);
}
