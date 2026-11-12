using System.Globalization;
using System.IO;
using System.Text.Json;
using Mixer.Audio.Dsp;

namespace Mixer.App;

/// <summary>
/// Настройки программы, а не схемы: settings.json рядом с graph.json (%AppData%\Mixer): язык, тема, свои пресеты,
/// трей, всплывающие подтверждения, помощник. Испорченный или недоступный файл не мешает запуску: берутся значения
/// по умолчанию. Автозапуск хранится не здесь, а в ветке Run реестра (<see cref="Ui.Autostart"/>).
/// </summary>
public sealed class Preferences
{
    /// <summary>Язык как в Windows.</summary>
    public const string Auto = "auto";

    public static readonly string[] Languages = [Auto, "en", "ru"];

    /// <summary>Темы выпуска 2.1 (v2, «Темы»); первая — по умолчанию.</summary>
    public static readonly string[] Themes = ["graphite", "daylight", "modular"];

    /// <summary>Содержимое settings.json. null у флага — значение по умолчанию (в файл не пишется).</summary>
    private sealed record Data(
        string? Language,
        List<StoredPreset>? Presets = null,
        string? Theme = null,
        bool? CloseToTray = null,
        bool? TrayHintShown = null,
        bool? HotkeyToasts = null,
        bool? DeviceNotifications = null,
        bool? SetupShown = null);

    /// <summary>Свой пресет цепочки обработки.</summary>
    public sealed record StoredPreset(string Name, ChainSettings Chain);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private Data _data = new(Auto);

    public Preferences(string path, CultureInfo windowsCulture)
    {
        Path = path;
        WindowsCulture = windowsCulture;
    }

    public string Path { get; }

    /// <summary>Язык интерфейса Windows (до того, как программа выставила свой).</summary>
    public CultureInfo WindowsCulture { get; }

    /// <summary>auto, en или ru.</summary>
    public string Language => _data.Language ?? Auto;

    /// <summary>Свои пресеты цепочки обработки (по имени).</summary>
    public IReadOnlyList<StoredPreset> Presets => _data.Presets ?? [];

    /// <summary>graphite, daylight или modular. Применяется после перезапуска, как язык.</summary>
    public string Theme => _data.Theme ?? Themes[0];

    /// <summary>Крестик окна сворачивает в трей (иначе — выход).</summary>
    public bool CloseToTray => _data.CloseToTray ?? true;

    /// <summary>Подсказка «программа в трее» уже показывалась (при первом закрытии крестиком).</summary>
    public bool TrayHintShown => _data.TrayHintShown ?? false;

    /// <summary>Срабатывание горячей клавиши подтверждается коротким всплывающим окном у трея.</summary>
    public bool HotkeyToasts => _data.HotkeyToasts ?? true;

    /// <summary>Уведомления Windows о потере и возврате устройства, пока окно свёрнуто.</summary>
    public bool DeviceNotifications => _data.DeviceNotifications ?? true;

    /// <summary>Помощник настройки уже предлагался при первом запуске.</summary>
    public bool SetupShown => _data.SetupShown ?? false;

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
            if (d is null) return p;
            p._data = d with
            {
                Language = d.Language is { } lang && Languages.Contains(lang) ? lang : Auto,
                Presets = d.Presets?.Where(x => !string.IsNullOrWhiteSpace(x.Name) && x.Chain is not null)
                    .Select(x => x with { Chain = x.Chain.Normalized() }).ToList(),
                Theme = d.Theme is { } t && Themes.Contains(t) ? t : null,
            };
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
        Write(_data with { Language = language });
    }

    /// <summary>Сохранить тему. Ошибка записи — исключение.</summary>
    public void SetTheme(string theme)
    {
        if (!Themes.Contains(theme)) throw new ArgumentOutOfRangeException(nameof(theme), theme, null);
        Write(_data with { Theme = theme == Themes[0] ? null : theme });
    }

    /// <summary>Сохранить свой пресет (то же имя — заменить). Ошибка записи — исключение.</summary>
    public void SavePreset(string name, ChainSettings chain)
    {
        var list = Presets.Where(x => !string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase)).ToList();
        list.Add(new StoredPreset(name, chain));
        Write(_data with { Presets = list });
    }

    public void SetCloseToTray(bool on) => Write(_data with { CloseToTray = on ? null : false });
    public void SetHotkeyToasts(bool on) => Write(_data with { HotkeyToasts = on ? null : false });
    public void SetDeviceNotifications(bool on) => Write(_data with { DeviceNotifications = on ? null : false });

    /// <summary>Флаги «уже показано»: ошибка записи не мешает работе (показ повторится в следующий раз).</summary>
    public void MarkTrayHintShown() => TryWrite(_data with { TrayHintShown = true });
    public void MarkSetupShown() => TryWrite(_data with { SetupShown = true });

    private void TryWrite(Data d)
    {
        try
        {
            Write(d);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _data = d; // в памяти запомнить: в этом запуске второй раз не показывать
        }
    }

    private void Write(Data d)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string tmp = Path + ".tmp";
        var clean = d with { Presets = d.Presets is { Count: > 0 } ? d.Presets : null };
        File.WriteAllText(tmp, JsonSerializer.Serialize(clean, Json));
        File.Move(tmp, Path, overwrite: true);
        _data = d;
    }

    /// <summary>Культура, с которой программа запустится при этой настройке (без --lang).</summary>
    public CultureInfo Resolve(string language) => AppOptions.PickCulture(language, WindowsCulture);
}
