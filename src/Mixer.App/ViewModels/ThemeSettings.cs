using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mixer.App.Ui;

namespace Mixer.App.ViewModels;

public sealed partial class ThemeRow : ObservableObject
{
    /// <summary>graphite, daylight или modular.</summary>
    public required string Code { get; init; }
    public required string Title { get; init; }
    public required string Text { get; init; }
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>
/// Настройки → «Тема» (макет «Theme · settings picker»). Выбор сразу сохраняется в settings.json, а применяется после
/// перезапуска, как язык: все ресурсы в XAML — StaticResource.
/// </summary>
public sealed partial class ThemeSettings : ObservableObject
{
    private readonly Preferences _prefs;
    private readonly Action _restart;
    private readonly string _running;

    /// <param name="running">Тема, с которой программа сейчас запущена.</param>
    public ThemeSettings(Preferences prefs, Action restart, string running)
    {
        _prefs = prefs;
        _restart = restart;
        _running = running;
        Options.Add(new ThemeRow { Code = ThemeManager.Graphite, Title = Strings.Theme_Graphite, Text = Strings.Theme_GraphiteText });
        Options.Add(new ThemeRow { Code = ThemeManager.Daylight, Title = Strings.Theme_Daylight, Text = Strings.Theme_DaylightText });
        Options.Add(new ThemeRow { Code = ThemeManager.Modular, Title = Strings.Theme_Modular, Text = Strings.Theme_ModularText });
        foreach (var o in Options) o.IsCurrent = o.Code == prefs.Theme;
        Update();
    }

    public ObservableCollection<ThemeRow> Options { get; } = new();

    [ObservableProperty] private string? _note;
    [ObservableProperty] private bool _needsRestart;

    [RelayCommand]
    private void Choose(ThemeRow row)
    {
        if (row.IsCurrent) return;
        try
        {
            _prefs.SetTheme(row.Code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NeedsRestart = false;
            Note = Fmt.F(Strings.Theme_SaveFailedFormat, ex.Message);
            return;
        }
        foreach (var o in Options) o.IsCurrent = o == row;
        Update();
    }

    private void Update()
    {
        NeedsRestart = _prefs.Theme != _running;
        Note = NeedsRestart ? Strings.Theme_RestartNote : null;
    }

    [RelayCommand]
    private void Restart() => _restart();
}

/// <summary>Узел-микрофон в выборе главного микрофона.</summary>
public sealed partial class MicChoice : ObservableObject
{
    public string? Id { get; init; }
    public required string Title { get; init; }
    [ObservableProperty] private bool _isCurrent;
}
