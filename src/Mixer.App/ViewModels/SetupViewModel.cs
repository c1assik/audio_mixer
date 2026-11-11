using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mixer.App.Ui;
using Mixer.Audio.Interop;
using Mixer.Core.Apps;
using Mixer.Core.Cables;
using Mixer.Core.Editing;
using Mixer.Core.Graph;

namespace Mixer.App.ViewModels;

/// <summary>Устройство в списке помощника.</summary>
public sealed partial class SetupDevice : ObservableObject
{
    public EndpointInfo? Device { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>Приложение в списке «Какие приложения держать отдельно».</summary>
public sealed partial class SetupAppRow : ObservableObject
{
    public required string Exe { get; init; }
    public required string Name { get; init; }
    public ImageSource? Icon { get; init; }
    public bool Playing { get; init; }
    public bool HasIcon => Icon is not null;
    [ObservableProperty] private bool _isChecked;
}

/// <summary>
/// Помощник настройки (v2, «Помощник настройки»; макет «Setup assistant, step 2»): наушники и микрофон → куда отдать
/// звук (с проверкой кабелей) → какие приложения держать отдельно. В конце собирает схему
/// (<see cref="SetupAssistant.Build"/>) одним шагом истории — Ctrl+Z вернёт прежнюю.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    public const int Steps = 3;
    private const string CableSite = "https://vb-audio.com/Cable/";

    private readonly MainViewModel _owner;
    private readonly DispatcherTimer _recheck;
    private List<EndpointInfo> _devices = [];
    private SourceCatalog? _catalog;

    public SetupViewModel(MainViewModel owner)
    {
        _owner = owner;
        // пока открыт шаг с кабелями, помощник сам замечает только что установленный кабель
        _recheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _recheck.Tick += (_, _) =>
        {
            if (Step == 2 && !Checking) CheckAgainCommand.Execute(null);
        };
        _recheck.Start();
        Autostart = Ui.Autostart.IsEnabled();
        LoadDevices(owner.ListDevicesAsync());
        LoadApps();
    }

    public ObservableCollection<SetupDevice> Outputs { get; } = new();
    public ObservableCollection<SetupDevice> Inputs { get; } = new();
    public ObservableCollection<SetupAppRow> Apps { get; } = new();

    [ObservableProperty] private int _step = 1;
    [ObservableProperty] private bool _headphonesOnly = true;
    [ObservableProperty] private bool _stream;
    [ObservableProperty] private bool _call;
    [ObservableProperty] private bool _checking;
    [ObservableProperty] private bool _appsLoading = true;
    [ObservableProperty] private string _cablesText = "";
    [ObservableProperty] private bool _cablesShort;
    /// <summary>Запускать микшер вместе с Windows (свёрнутым в трей).</summary>
    [ObservableProperty] private bool _autostart;

    public string StepText => Fmt.F(Strings.Setup_StepFormat, Step, Steps);
    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool CanBack => Step > 1;
    public bool IsLast => Step == Steps;
    public bool NotLast => Step < Steps;
    public bool ReplacesGraph => _owner.Graph.Nodes.Count > 0;
    public bool NoApps => !AppsLoading && Apps.Count == 0;
    public bool NeedsCables => SetupAssistant.CablesNeeded(Choices()) > 0;
    /// <summary>Индикатор шагов: заполнены ли полоски 1…3.</summary>
    public bool Bar2 => Step >= 2;
    public bool Bar3 => Step >= 3;

    public SetupDevice? Headphones => Outputs.FirstOrDefault(d => d.IsCurrent);
    public SetupDevice? Microphone => Inputs.FirstOrDefault(d => d.IsCurrent);

    partial void OnStepChanged(int value)
    {
        foreach (string p in new[] { nameof(StepText), nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(CanBack), nameof(IsLast), nameof(NotLast), nameof(Bar2), nameof(Bar3) })
            OnPropertyChanged(p);
        NextCommand.NotifyCanExecuteChanged();
    }

    partial void OnStreamChanged(bool value) => UpdateCables();
    partial void OnCallChanged(bool value) => UpdateCables();
    partial void OnHeadphonesOnlyChanged(bool value) => NextCommand.NotifyCanExecuteChanged();
    partial void OnAppsLoadingChanged(bool value) => OnPropertyChanged(nameof(NoApps));

    private async void LoadDevices(Task<List<EndpointInfo>> task)
    {
        try
        {
            _devices = await task;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _owner.Notify(Fmt.F(Strings.Palette_DevicesFailedFormat, ex.Message), error: true);
            return;
        }
        var table = CableTable.Default;
        bool Usable(EndpointInfo d) => !d.IsVoicemeeter && table.Match(d) is null;
        Fill(Outputs, _devices.Where(d => d.Flow == EDataFlow.Render && Usable(d)));
        Fill(Inputs, _devices.Where(d => d.Flow == EDataFlow.Capture && Usable(d)));
        UpdateCables();
        NextCommand.NotifyCanExecuteChanged();
    }

    private static void Fill(ObservableCollection<SetupDevice> list, IEnumerable<EndpointInfo> devices)
    {
        list.Clear();
        foreach (var d in devices.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name))
            list.Add(new SetupDevice { Device = d, Title = Fmt.ShortDevice(d.Name), Subtitle = d.Name, IsCurrent = list.Count == 0 && d.IsDefault });
        if (list.Count > 0 && !list.Any(x => x.IsCurrent)) list[0].IsCurrent = true;
        list.Add(new SetupDevice { Title = Strings.Setup_None });
    }

    private async void LoadApps()
    {
        try
        {
            _catalog = await _owner.QueryCatalogAsync();
            foreach (var a in _catalog.Apps) AppIcons.Remember(a.Exe, a.Path);
            var skip = AppNames.StreamApps.Concat(AppNames.CallApps).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var a in _catalog.Apps.Where(a => !skip.Contains(a.Exe)).OrderByDescending(a => a.HasSession).ThenBy(a => a.Name).Take(24))
                Apps.Add(new SetupAppRow { Exe = a.Exe, Name = a.Name, Icon = AppIcons.FromPath(a.Path), Playing = a.Playing });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // без списка помощник всё равно соберёт схему, приложения можно добавить потом
            App.Log?.Write("setup", "app list failed: " + ex.Message);
        }
        finally
        {
            AppsLoading = false;
        }
    }

    private SetupChoices Choices() => new()
    {
        Headphones = Headphones?.Device,
        Microphone = Microphone?.Device,
        HeadphonesOnly = HeadphonesOnly,
        Stream = Stream,
        Call = Call,
    };

    /// <summary>Хватает ли кабелей на стрим и звонок: «Нужно 2 виртуальных кабеля. Найдено: 1 — CABLE (VB-Audio)».</summary>
    private void UpdateCables()
    {
        var c = Choices();
        int need = SetupAssistant.CablesNeeded(c);
        var have = SetupAssistant.UsableCables(_devices);
        string names = string.Join(", ", have.Select(p => p.Label));
        if (need == 0)
        {
            CablesText = have.Count > 0 ? Fmt.F(Strings.Setup_CablesOkFormat, names) : "";
            CablesShort = false;
        }
        else if (have.Count >= need)
        {
            CablesText = Fmt.F(Strings.Setup_CablesOkFormat, names);
            CablesShort = false;
        }
        else
        {
            string found = have.Count == 0 ? "0" : $"{have.Count} — {names}";
            CablesText = Fmt.F(Strings.Setup_CablesNeedFormat, Fmt.Plural(need, Strings.Plural_Cable), found) + " "
                         + Fmt.F(Strings.Setup_CablesInstallFormat, Fmt.Plural(need - have.Count, Strings.Plural_Cable));
            CablesShort = true;
        }
        OnPropertyChanged(nameof(NeedsCables));
        NextCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void PickOutput(SetupDevice d)
    {
        foreach (var x in Outputs) x.IsCurrent = x == d;
        OnPropertyChanged(nameof(Headphones));
    }

    [RelayCommand]
    private void PickInput(SetupDevice d)
    {
        foreach (var x in Inputs) x.IsCurrent = x == d;
        OnPropertyChanged(nameof(Microphone));
    }

    [RelayCommand]
    private async Task CheckAgain()
    {
        if (Checking) return;
        Checking = true;
        try
        {
            _devices = await _owner.ListDevicesAsync();
            UpdateCables();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CablesText = Fmt.F(Strings.Palette_DevicesFailedFormat, ex.Message);
        }
        finally
        {
            Checking = false;
        }
    }

    [RelayCommand]
    private void OpenCableSite()
    {
        try
        {
            Process.Start(new ProcessStartInfo(CableSite) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _owner.Notify(ex.Message, error: true);
        }
    }

    /// <summary>Шаг 2 — что-то выбрано и кабелей хватает (без кабелей можно только «Только наушники»).</summary>
    private bool CanNext() => Step switch
    {
        2 => (HeadphonesOnly || Stream || Call) && !CablesShort,
        _ => true,
    };

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        if (Step < Steps) Step++;
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > 1) Step--;
    }

    [RelayCommand]
    private void Skip()
    {
        _recheck.Stop();
        _owner.Preferences?.MarkSetupShown();
        _owner.CloseOverlay();
    }

    /// <summary>Собрать схему: одна правка истории, затем автозапуск по галочке и сообщение, что сделать в приложениях.</summary>
    [RelayCommand]
    private void Finish()
    {
        _recheck.Stop();
        var running = _catalog?.Apps.Select(a => a.Exe).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var choices = Choices() with
        {
            SeparateApps = Apps.Where(a => a.IsChecked).Select(a => new SetupApp(a.Exe, a.Name)).ToList(),
            // Discord всегда; остальные приложения для звонков — если запущены
            CallApps = AppNames.CallApps.Where(e => e == "discord.exe" || running.Contains(e)).ToList(),
        };
        var result = SetupAssistant.Build(_owner.Graph, choices, SetupAssistant.UsableCables(_devices), MainViewModel.SourceColors);
        _owner.ApplySetup(result, choices, Autostart);
    }

    public void Dispose() => _recheck.Stop();
}
