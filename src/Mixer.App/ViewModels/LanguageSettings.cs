using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mixer.App.Ui;

namespace Mixer.App.ViewModels;

public sealed partial class LanguageRow : ObservableObject
{
    /// <summary>auto, en или ru (<see cref="Preferences.Languages"/>).</summary>
    public required string Code { get; init; }
    public required string Title { get; init; }
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>
/// Настройки → «Язык». Выбор сразу сохраняется в settings.json, а применяется после перезапуска:
/// строки интерфейса берутся при создании элементов, на лету их не подменить.
/// </summary>
public sealed partial class LanguageSettings : ObservableObject
{
    private readonly Preferences _prefs;
    private readonly Action _restart;

    public LanguageSettings(Preferences prefs, Action restart)
    {
        _prefs = prefs;
        _restart = restart;
        foreach (string code in Preferences.Languages)
            Options.Add(new LanguageRow { Code = code, Title = TitleOf(code), IsCurrent = code == prefs.Language });
        Update();
    }

    public ObservableCollection<LanguageRow> Options { get; } = new();

    /// <summary>Пояснение под выбором: нужен перезапуск или запись не удалась; null — пояснять нечего.</summary>
    [ObservableProperty] private string? _note;
    [ObservableProperty] private bool _needsRestart;

    /// <summary>Названия языков — на самих этих языках, чтобы свой язык нашёлся в любом интерфейсе.</summary>
    private static string TitleOf(string code) => code == Preferences.Auto
        ? Strings.Settings_LanguageAuto
        : Fmt.Capitalize(CultureInfo.GetCultureInfo(code).NativeName);

    [RelayCommand]
    private void Choose(LanguageRow row)
    {
        if (row.IsCurrent) return;
        try
        {
            _prefs.SetLanguage(row.Code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NeedsRestart = false;
            Note = Fmt.F(Strings.Settings_LanguageSaveFailedFormat, ex.Message);
            return;
        }
        foreach (var o in Options) o.IsCurrent = o == row;
        Update();
    }

    /// <summary>Перезапуск нужен, если с этой настройкой программа запустится на другом языке, чем сейчас.</summary>
    private void Update()
    {
        NeedsRestart = _prefs.Resolve(_prefs.Language).Name != CultureInfo.CurrentUICulture.Name;
        Note = NeedsRestart ? Strings.Settings_LanguageRestartNote : null;
    }

    [RelayCommand]
    private void Restart() => _restart();
}
