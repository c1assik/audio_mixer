using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Mixer.App.Ui;
using Mixer.Audio.Interop;
using Mixer.Core.Apps;
using Mixer.Core.Config;
using Mixer.Core.Graph;
using Mixer.Core.Sys;
using Mixer.Plugins.Catalog;

namespace Mixer.App.ViewModels;

public enum PaletteMode { Sources, Outputs, Plugins }

/// <summary>Строка палитры: заголовок раздела или то, что можно добавить.</summary>
public sealed class PaletteRow
{
    public string? Header { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string GlyphKey { get; init; } = "IconApp";
    public ImageSource? Icon { get; init; }
    public bool Playing { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>Почему нельзя добавить (подсказка у недоступной строки).</summary>
    public string? Reason { get; init; }
    public NodeConfig? Template { get; init; }
    /// <summary>Выбор плагина: эффект из каталога.</summary>
    public CatalogEntry? Plugin { get; init; }
    /// <summary>«Не загрузился»: модуль, который можно просканировать с окном (без таймаута).</summary>
    public string? Rescan { get; init; }
    /// <summary>Что ещё находит строку поиском: полное имя устройства, exe.</summary>
    public string Keywords { get; init; } = "";

    public bool IsHeader => Header is not null;
    public bool HasIcon => Icon is not null;
    public Geometry Glyph => (Geometry)System.Windows.Application.Current.Resources[GlyphKey];
    internal string Search => $"{Title} {Subtitle} {Keywords}";
}

/// <summary>
/// Палитра «Добавить источник / выход» (макет «Палитра»): поиск по устройствам и приложениям, сверху приложения,
/// у которых сейчас есть звук. ↑↓ — выбрать, Enter — добавить, Esc — закрыть.
/// </summary>
public sealed partial class PaletteViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly double? _atY;
    private List<PaletteRow> _all = [];

    public PaletteViewModel(MainViewModel owner, PaletteMode mode, double? atY, Task<SourceCatalog> catalog)
    {
        _owner = owner;
        Mode = mode;
        _atY = atY;
        Load(catalog);
    }

    /// <summary>
    /// Выбор плагина (VST, «Выбор плагина»): каталог из кэша сразу, скан новых и изменившихся модулей — в фоне, найденные
    /// появляются по мере готовности. Группы: «Недавние», категории плагинов, «Не загрузились» (Enter — скан с окном),
    /// «Не поддерживается».
    /// </summary>
    public PaletteViewModel(MainViewModel owner, PluginTarget target)
    {
        _owner = owner;
        Mode = PaletteMode.Plugins;
        Target = target;
        Loading = false;
        var catalog = owner.Catalog;
        var ui = System.Windows.Application.Current.Dispatcher;
        Action<ScanProgress> progress = p => ui.BeginInvoke(() =>
        {
            ScanText = p.Checked < p.Total ? Fmt.F(Strings.Plugin_ScanProgressFormat, p.Checked, p.Total) : null;
            if (p.Module is not null || p.Checked == p.Total) LoadPlugins();
        });
        catalog.Progress += progress;
        LoadPlugins();
        var folders = owner.PluginFolders.ToList();
        Scan = Task.Run(async () =>
        {
            try { await catalog.RefreshAsync(folders); }
            catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                _ = ui.BeginInvoke(() => Error = Fmt.F(Strings.Plugin_ScanFailedFormat, ex.Message));
            }
            finally
            {
                catalog.Progress -= progress;
            }
        });
    }

    /// <summary>Фоновый скан выбора плагина (тесты ждут его).</summary>
    internal Task Scan { get; } = Task.CompletedTask;

    public PaletteMode Mode { get; }
    public PluginTarget? Target { get; }
    public string Title => Mode switch
    {
        PaletteMode.Sources => Strings.Palette_Title,
        PaletteMode.Outputs => Strings.Palette_TitleOutput,
        _ => Fmt.F(Strings.Plugin_PickerTitleFormat, Target!.Node.Name),
    };
    public string Placeholder => Mode switch
    {
        PaletteMode.Sources => Strings.Palette_SearchPlaceholder,
        PaletteMode.Outputs => Strings.Palette_SearchPlaceholderOutput,
        _ => Strings.Plugin_SearchPlaceholder,
    };
    /// <summary>«Проверено 34 из 61», пока идёт скан.</summary>
    [ObservableProperty] private string? _scanText;
    public ObservableCollection<PaletteRow> Rows { get; } = new();

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private bool _loading = true;
    [ObservableProperty] private string? _error;

    public bool IsEmpty => !Loading && Error is null && !Rows.Any(r => !r.IsHeader);

    partial void OnQueryChanged(string value) => Filter();

    private async void Load(Task<SourceCatalog> task)
    {
        try
        {
            var c = await task;
            foreach (var a in c.Apps) AppIcons.Remember(a.Exe, a.Path);
            _all = Mode == PaletteMode.Sources ? SourceRows(c) : OutputRows(c);
        }
        catch (Exception ex)
        {
            Error = Fmt.F(Strings.Palette_DevicesFailedFormat, ex.Message);
        }
        finally
        {
            Loading = false;
        }
        Filter();
    }

    private bool OnGraph(string? endpointId, NodeKind kind) =>
        endpointId is not null && _owner.Graph.Nodes.Any(n => n.Kind == kind && string.Equals(n.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Строки «Устройство по умолчанию»: узел следует за Windows. «Для связи» — только если это другое устройство
    /// (или такой узел уже есть), иначе в палитре две одинаковые строки.
    /// </summary>
    private IEnumerable<PaletteRow> DefaultRows(SourceCatalog c, NodeKind kind, string glyph)
    {
        var flow = kind == NodeKind.DeviceInput ? EDataFlow.Capture : EDataFlow.Render;
        var console = c.Devices.FirstOrDefault(d => d.Flow == flow && d.IsDefault);
        var comms = c.Devices.FirstOrDefault(d => d.Flow == flow && d.IsDefaultComms);
        bool OnGraph(DefaultRole r) => _owner.Graph.Nodes.Any(n => n.Kind == kind && n.DefaultRole == r);

        PaletteRow Row(DefaultRole role, EndpointInfo? now)
        {
            string current = now is null ? Strings.Palette_DefaultNowNone : Fmt.F(Strings.Palette_DefaultNowFormat, Fmt.ShortDevice(now.Name));
            return new PaletteRow
            {
                Title = kind == NodeKind.DeviceInput
                    ? role == DefaultRole.Communications ? Strings.Palette_DefaultMicComms : Strings.Palette_DefaultMic
                    : Fmt.DefaultNodeName(NodeKind.DeviceOutput, role),
                Subtitle = OnGraph(role) ? Strings.Palette_AlreadyOnGraph
                    : kind == NodeKind.Loopback ? Fmt.F(Strings.Palette_DefaultLoopbackFormat, current)
                    : Fmt.F(Strings.Palette_DefaultFollowsFormat, current),
                GlyphKey = glyph,
                // без имени устройства: поиск по нему должен находить само устройство, а не ещё и эту строку
                Keywords = Strings.Palette_KeywordsDefault + (role == DefaultRole.Communications ? " " + Strings.Palette_KeywordsComms : ""),
                Template = new NodeConfig { Id = "", Kind = kind, Name = Fmt.DefaultNodeName(kind, role), DefaultRole = role },
            };
        }

        yield return Row(DefaultRole.Console, console);
        if (comms is not null && !string.Equals(comms.Id, console?.Id, StringComparison.OrdinalIgnoreCase) || OnGraph(DefaultRole.Communications))
            yield return Row(DefaultRole.Communications, comms);
    }

    private List<PaletteRow> SourceRows(SourceCatalog c)
    {
        var rows = new List<PaletteRow>();
        string? pl = c.ProcessLoopbackError;
        var graphExes = _owner.Graph.Nodes.Where(n => n.Kind == NodeKind.App && n.Exe is not null)
            .Select(n => ProcessSnapshot.NormalizeExe(n.Exe!)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        PaletteRow App(AppEntry a) => new()
        {
            Title = a.Name,
            Subtitle = a.Exe + (a.Processes > 1 ? " · " + Fmt.Plural(a.Processes, Strings.Plural_Process) : "")
                       + (graphExes.Contains(a.Exe) ? " · " + Strings.Palette_OnGraph : a.HasSession && !a.Playing ? " · " + Strings.Palette_SilentNow : ""),
            GlyphKey = "IconApp",
            Keywords = a.Exe,
            Icon = AppIcons.FromPath(a.Path),
            Playing = a.Playing,
            Enabled = pl is null,
            Reason = pl,
            Template = new NodeConfig { Id = "", Kind = NodeKind.App, Name = a.Name, Exe = a.Exe, TakeOver = true },
        };

        var withAudio = c.Apps.Where(a => a.HasSession).ToList();
        var other = c.Apps.Where(a => !a.HasSession).ToList();
        if (withAudio.Count > 0)
        {
            rows.Add(new PaletteRow { Header = Strings.Palette_GroupPlayingNow });
            rows.AddRange(withAudio.Select(App));
        }
        if (other.Count > 0)
        {
            rows.Add(new PaletteRow { Header = Strings.Palette_GroupOtherApps });
            rows.AddRange(other.Select(App));
        }

        // кабели одной строкой на кабель: виртуальный вход (приложение играет в кабель, микшер читает его выход)
        var cables = CableRows(c, NodeKind.DeviceInput);
        if (cables.Count > 0)
        {
            rows.Add(new PaletteRow { Header = Strings.Palette_GroupCables });
            rows.AddRange(cables);
        }
        var cableEnds = cables.Select(r => r.Template!.EndpointId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var inputs = c.Devices.Where(d => d.Flow == EDataFlow.Capture && !cableEnds.Contains(d.Id)).OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name).ToList();
        rows.Add(new PaletteRow { Header = Strings.Palette_GroupInputs });
        rows.AddRange(DefaultRows(c, NodeKind.DeviceInput, "IconMic"));
        {
            foreach (var d in inputs)
            {
                bool cable = FeedbackRules.IsVirtualCable(d.Name);
                rows.Add(new PaletteRow
                {
                    Title = Fmt.ShortDevice(d.Name),
                    Subtitle = OnGraph(d.Id, NodeKind.DeviceInput) ? Strings.Palette_AlreadyOnGraph
                        : cable ? Strings.Palette_VirtualCableInput
                        : d.IsDefault ? Fmt.F(Strings.Palette_DefaultMicDeviceFormat, d.Name) : d.Name,
                    GlyphKey = cable ? "IconPlug" : "IconMic",
                    Keywords = d.Name,
                    Template = new NodeConfig { Id = "", Kind = NodeKind.DeviceInput, Name = Fmt.ShortDevice(d.Name), EndpointId = d.Id, EndpointName = d.Name },
                });
            }
        }

        var outputs = c.Devices.Where(d => d.Flow == EDataFlow.Render).OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name).ToList();
        rows.Add(new PaletteRow { Header = Strings.Palette_GroupLoopback });
        rows.AddRange(DefaultRows(c, NodeKind.Loopback, "IconSpeaker"));
        {
            foreach (var d in outputs)
            {
                bool vaioOff = d.IsVoicemeeter && !c.VoicemeeterRunning;
                rows.Add(new PaletteRow
                {
                    Title = Fmt.ShortDevice(d.Name),
                    Subtitle = OnGraph(d.Id, NodeKind.Loopback) ? Strings.Palette_AlreadyOnGraph : Fmt.F(Strings.Palette_LoopbackHintFormat, Fmt.ShortDevice(d.Name)),
                    GlyphKey = "IconSpeaker",
                    Keywords = d.Name,
                    Enabled = !vaioOff,
                    Reason = vaioOff ? Strings.Palette_VaioOff : null,
                    Template = new NodeConfig { Id = "", Kind = NodeKind.Loopback, Name = Fmt.F(Strings.NodeName_LoopbackFormat, Fmt.ShortDevice(d.Name)), EndpointId = d.Id, EndpointName = d.Name },
                });
            }
        }

        bool hasSystem = _owner.Graph.Nodes.Any(n => n.Kind == NodeKind.System);
        rows.Add(new PaletteRow { Header = Strings.Palette_GroupSpecial });
        rows.Add(new PaletteRow
        {
            Title = Strings.NodeType_SystemAudio,
            Subtitle = hasSystem ? Strings.Palette_SystemOnGraph : Strings.Palette_SystemHint,
            GlyphKey = "IconSystem",
            Enabled = pl is null && !hasSystem,
            Reason = pl ?? (hasSystem ? Strings.Palette_SystemOnlyOne : null),
            Template = new NodeConfig { Id = "", Kind = NodeKind.System, Name = Strings.NodeType_SystemAudio },
        });
        return rows;
    }

    private List<PaletteRow> OutputRows(SourceCatalog c)
    {
        var rows = new List<PaletteRow>();
        // кабели одной строкой на кабель (раздел — после выходов): виртуальный микрофон, микшер играет в кабель,
        // приложение читает его выход
        var cables = CableRows(c, NodeKind.DeviceOutput);
        var cableEnds = cables.Select(r => r.Template!.EndpointId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        rows.Add(new PaletteRow { Header = Strings.Palette_GroupOutputs });
        rows.AddRange(DefaultRows(c, NodeKind.DeviceOutput, "IconHeadphones"));
        string? sink = _owner.Graph.SinkEndpoint;
        foreach (var d in c.Devices.Where(d => d.Flow == EDataFlow.Render && !cableEnds.Contains(d.Id)).OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name))
        {
            bool cable = FeedbackRules.IsVirtualCable(d.Name);
            bool vaioOff = d.IsVoicemeeter && !c.VoicemeeterRunning;
            bool isSink = string.Equals(d.Id, sink, StringComparison.OrdinalIgnoreCase);
            rows.Add(new PaletteRow
            {
                Title = Fmt.ShortDevice(d.Name),
                Subtitle = isSink ? Strings.Palette_IsSink
                    : OnGraph(d.Id, NodeKind.DeviceOutput) ? Strings.Palette_AlreadyOnGraph
                    : cable ? Strings.Palette_CableOutput
                    : d.IsDefault ? Fmt.F(Strings.Palette_DefaultDeviceFormat, d.Name) : d.Name,
                GlyphKey = cable ? "IconBroadcast" : "IconHeadphones",
                Keywords = d.Name,
                Enabled = !vaioOff,
                Reason = vaioOff ? Strings.Palette_VaioOff : null,
                Template = new NodeConfig { Id = "", Kind = NodeKind.DeviceOutput, Name = Fmt.ShortDevice(d.Name), EndpointId = d.Id, EndpointName = d.Name },
            });
        }
        if (cables.Count > 0)
        {
            rows.Add(new PaletteRow { Header = Strings.Palette_GroupCables });
            rows.AddRange(cables);
        }
        return rows;
    }

    /// <summary>
    /// Строки кабелей (v2, «Виртуальные кабели»): кабель — одна сущность с ролью. В выходах — виртуальный микрофон
    /// (узел пишет в основной вход кабеля), в источниках — виртуальный вход (узел читает выход кабеля).
    /// </summary>
    private List<PaletteRow> CableRows(SourceCatalog c, NodeKind kind)
    {
        var rows = new List<PaletteRow>();
        bool mic = kind == NodeKind.DeviceOutput;
        string? sink = _owner.Graph.SinkEndpoint;
        foreach (var p in Mixer.Core.Cables.CableTable.Default.Pairs(c.Devices))
        {
            var end = mic ? p.Input : p.Output;
            if (end is null) continue;
            bool isSink = mic && string.Equals(end.Id, sink, StringComparison.OrdinalIgnoreCase);
            rows.Add(new PaletteRow
            {
                Title = p.Label,
                Subtitle = isSink ? Strings.Palette_IsSink
                    : OnGraph(end.Id, kind) ? Strings.Palette_AlreadyOnGraph
                    : !p.Complete ? Strings.Palette_CableHalf
                    : Fmt.F(mic ? Strings.Palette_CableMicFormat : Strings.Palette_CableInputFormat, Fmt.ShortDevice(p.Input!.Name), Fmt.ShortDevice(p.Output!.Name)),
                GlyphKey = mic ? "IconBroadcast" : "IconPlug",
                Keywords = string.Join(" ", p.Inputs.Select(d => d.Name).Append(p.Output?.Name ?? "")),
                Template = new NodeConfig
                {
                    Id = "", Kind = kind, Name = p.Name, EndpointId = end.Id, EndpointName = end.Name,
                    Cable = new CableLink { PairId = p.Id, Role = mic ? CableRole.VirtualMic : CableRole.VirtualInput },
                },
            });
        }
        return rows;
    }

    internal void LoadPlugins()
    {
        var c = _owner.Catalog;
        var effects = c.Effects;
        PaletteRow Row(CatalogEntry e) => new()
        {
            Title = e.Class.Name,
            Subtitle = e.Class.Vendor + " · " + (e.Module.Signature switch
            {
                { Status: SignatureStatus.Valid, Signer: { } who } => Fmt.F(Strings.Plugin_SignedFormat, who),
                { Status: SignatureStatus.Invalid } => Strings.Plugin_SignatureInvalid,
                _ => Strings.Plugin_NotSigned,
            }),
            GlyphKey = "IconPlugin",
            Keywords = e.Class.Vendor + " " + string.Join(" ", e.Class.Categories),
            Plugin = e,
        };
        var rows = new List<PaletteRow>();
        var recent = (_owner.Preferences?.RecentPlugins ?? []).Select(id => effects.FirstOrDefault(e => e.Class.ClassId.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .OfType<CatalogEntry>().ToList();
        if (recent.Count > 0)
        {
            rows.Add(new PaletteRow { Header = Strings.Plugin_GroupRecent });
            rows.AddRange(recent.Select(Row));
        }
        foreach (var g in effects.GroupBy(e => e.Class.Categories.FirstOrDefault() ?? Strings.Plugin_GroupOther)
                     .OrderBy(g => g.Key == Strings.Plugin_GroupOther).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            rows.Add(new PaletteRow { Header = g.Key.ToUpper(System.Globalization.CultureInfo.CurrentCulture) });
            rows.AddRange(g.Select(Row));
        }
        if (c.NotLoaded is { Count: > 0 } failed)
        {
            rows.Add(new PaletteRow { Header = Strings.Plugin_GroupNotLoaded });
            rows.AddRange(failed.Select(m => new PaletteRow
            {
                Title = m.FileName,
                Subtitle = Problem(m.Problem, m.Detail) + " · " + Strings.Plugin_ScanWithWindow,
                GlyphKey = "IconWarn",
                Keywords = m.Path,
                Rescan = m.Path,
            }));
        }
        if (c.NotSupported is { Count: > 0 } bad)
        {
            rows.Add(new PaletteRow { Header = Strings.Plugin_GroupNotSupported });
            rows.AddRange(bad.Select(b => new PaletteRow
            {
                Title = b.Name,
                Subtitle = Problem(b.Problem, b.Detail),
                GlyphKey = "IconWarn",
                Keywords = b.Path,
                Enabled = false,
                Reason = b.Path,
            }));
        }
        _all = rows;
        Filter();
    }

    internal static string Problem(ScanProblem p, string? detail) => p switch
    {
        ScanProblem.Timeout => Strings.Plugin_ProblemTimeout,
        ScanProblem.Crashed => Strings.Plugin_ProblemCrashed,
        ScanProblem.LoadFailed => Fmt.F(Strings.Plugin_ProblemLoadFailedFormat, detail ?? "?"),
        ScanProblem.Vst2 => Strings.Plugin_ProblemVst2,
        ScanProblem.Bits32 => Strings.Plugin_ProblemBits32,
        ScanProblem.NoX64Binary => Strings.Plugin_ProblemNoX64,
        ScanProblem.Instrument => Strings.Plugin_ProblemInstrument,
        _ => Strings.Plugin_ProblemNoEffects,
    };

    private async Task RescanAsync(string path)
    {
        try { await _owner.Catalog.RescanAsync(path, withWindow: true); }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Error = Fmt.F(Strings.Plugin_ScanFailedFormat, ex.Message);
        }
        ScanText = null;
        LoadPlugins();
    }

    private void Filter()
    {
        string q = Query.Trim();
        Rows.Clear();
        PaletteRow? header = null;
        foreach (var r in _all)
        {
            if (r.IsHeader)
            {
                header = r;
                continue;
            }
            if (q.Length > 0 && !r.Search.Contains(q, StringComparison.CurrentCultureIgnoreCase)) continue;
            if (header is not null)
            {
                Rows.Add(header);
                header = null;
            }
            Rows.Add(r);
        }
        SelectedIndex = FirstSelectable(0, +1);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private int FirstSelectable(int from, int step)
    {
        for (int i = from; i >= 0 && i < Rows.Count; i += step)
            if (!Rows[i].IsHeader && Rows[i].Enabled) return i;
        return -1;
    }

    public void Move(int step)
    {
        int next = FirstSelectable(SelectedIndex < 0 ? 0 : SelectedIndex + step, step);
        if (next >= 0) SelectedIndex = next;
    }

    public void Commit(PaletteRow? row = null)
    {
        row ??= SelectedIndex >= 0 && SelectedIndex < Rows.Count ? Rows[SelectedIndex] : null;
        if (row is null || !row.Enabled) return;
        if (row.Rescan is { } path)
        {
            ScanText = Fmt.F(Strings.Plugin_ScanningWithWindowFormat, row.Title);
            _ = RescanAsync(path);
            return;
        }
        if (row.Plugin is { } plugin && Target is { } target)
        {
            _owner.CloseOverlay();
            _owner.AddPlugin(target, plugin);
            return;
        }
        if (row.Template is null) return;
        _owner.CloseOverlay();
        _owner.AddNode(row.Template, _atY);
    }
}
