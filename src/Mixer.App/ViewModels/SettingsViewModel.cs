using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mixer.App.Ui;
using Mixer.Audio.Interop;
using Mixer.Core.Apps;
using Mixer.Core.Graph;

namespace Mixer.App.ViewModels;

public sealed partial class SinkRow : ObservableObject
{
    public required EndpointInfo Device { get; init; }
    public string Title => Fmt.ShortDevice(Device.Name);
    public string Subtitle { get; init; } = "";
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isChecking;
}

/// <summary>
/// Настройки: заглушка для «Забрать себе». Устройство проверяется пробным потоком до того, как его назначить:
/// заглушку, которая не принимает потоки, назначить нельзя (критерий приёмки), причина показывается здесь же.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    public SettingsViewModel(MainViewModel owner)
    {
        _owner = owner;
        Language = owner.Preferences is { } p ? new LanguageSettings(p, owner.RequestRestart) : null;
        Refresh();
        Load(owner.ListDevicesAsync());
    }

    /// <summary>Устройства читаются заново в управляющем потоке: список в статусе хоста может быть ещё пуст.</summary>
    private async void Load(Task<List<EndpointInfo>> devices)
    {
        List<EndpointInfo> list;
        try
        {
            list = await devices;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Verdict = SinkVerdict.Rejected;
            Status = Fmt.F(Strings.Palette_DevicesFailedFormat, ex.Message);
            return;
        }
        var owner = _owner;
        string? sink = owner.Graph.SinkEndpoint;
        foreach (var d in list.Where(d => d.Flow == EDataFlow.Render).OrderByDescending(d => FeedbackRules.IsVirtualCable(d.Name)).ThenBy(d => d.Name))
        {
            var tags = new List<string>();
            if (FeedbackRules.IsVirtualCable(d.Name)) tags.Add(Strings.Settings_TagCable);
            if (d.IsVoicemeeter) tags.Add(Strings.Settings_TagVaio);
            if (d.IsDefault) tags.Add(Strings.Settings_TagDefault);
            if (owner.Graph.Nodes.Any(n => n.IsOutput && string.Equals(n.EndpointId, d.Id, StringComparison.OrdinalIgnoreCase))) tags.Add(Strings.Settings_TagOnGraph);
            Devices.Add(new SinkRow
            {
                Device = d,
                Subtitle = tags.Count > 0 ? string.Join(" · ", tags) : d.Name,
                IsCurrent = string.Equals(d.Id, sink, StringComparison.OrdinalIgnoreCase),
            });
        }
    }

    public ObservableCollection<SinkRow> Devices { get; } = new();

    /// <summary>Выбор языка; null — без файла настроек.</summary>
    public LanguageSettings? Language { get; }

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private SinkVerdict? _verdict;
    [ObservableProperty] private bool _busy;

    public bool HasSink => _owner.Graph.SinkEndpoint is not null;
    public string LoopbackText => _owner.Host?.Status.ProcessLoopbackError ?? Strings.Settings_LoopbackAvailable;
    public string PolicyText => _owner.Host?.Status.PolicyError is { } e
        ? Fmt.F(Strings.Settings_PolicyUnavailableFormat, e)
        : Strings.Settings_PolicyInfo;

    private void Refresh()
    {
        var sink = _owner.Host?.Status.Sink;
        if (_owner.Graph.SinkEndpoint is null)
        {
            Verdict = null;
            Status = Strings.Settings_NoSink;
        }
        else if (sink is null)
        {
            Verdict = null;
            Status = _owner.IsOffline ? Strings.Settings_OfflineNotChecked : Strings.Settings_WillCheck;
        }
        else
        {
            Verdict = sink.Verdict;
            Status = sink.Verdict switch
            {
                SinkVerdict.Ok => Fmt.F(Strings.Settings_SinkOkFormat, sink.Endpoint?.Name),
                SinkVerdict.Warning => Fmt.F(Strings.Settings_SinkWarningFormat, sink.Message),
                _ => Fmt.F(Strings.Settings_SinkRejectedFormat, sink.Message),
            };
        }
        OnPropertyChanged(nameof(HasSink));
    }

    [RelayCommand]
    private async Task Choose(SinkRow row)
    {
        if (Busy || row.IsCurrent) return;
        if (_owner.Host is null)
        {
            Apply(row);
            Status = Strings.Settings_OfflineAssigned;
            return;
        }
        Busy = true;
        row.IsChecking = true;
        Verdict = null;
        Status = Fmt.F(Strings.Settings_CheckingFormat, row.Title);
        try
        {
            var r = await _owner.Host.ProbeSinkAsync(row.Device.Id);
            Verdict = r.Verdict;
            if (!r.Usable)
            {
                Status = Fmt.F(Strings.Settings_CannotAssignFormat, r.Message);
                return;
            }
            Apply(row);
            Status = r.Verdict == SinkVerdict.Ok ? Fmt.F(Strings.Settings_AssignedFormat, row.Title) : Fmt.F(Strings.Settings_AssignedWarningFormat, r.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Verdict = SinkVerdict.Rejected;
            Status = Fmt.F(Strings.Settings_CheckFailedFormat, ex.Message);
        }
        finally
        {
            row.IsChecking = false;
            Busy = false;
        }
    }

    private void Apply(SinkRow row)
    {
        _owner.SetSink(row.Device.Id);
        foreach (var d in Devices) d.IsCurrent = d == row;
        OnPropertyChanged(nameof(HasSink));
    }

    [RelayCommand]
    private void Clear()
    {
        _owner.SetSink(null);
        foreach (var d in Devices) d.IsCurrent = false;
        Refresh();
    }

    [RelayCommand]
    private void OpenVolumeMixer() => _owner.OpenVolumeMixerCommand.Execute(null);

    [RelayCommand]
    private void OpenSetup() => _owner.OpenSetup();

    [RelayCommand]
    private void Close() => _owner.CloseOverlay();
}
