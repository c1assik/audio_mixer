using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mixer.App.Ui;
using Mixer.Audio.Interop;
using Mixer.Core.Config;
using Mixer.Core.Editing;
using Mixer.Core.Host;
using Mixer.Core.Sys;

namespace Mixer.App.ViewModels;

/// <summary>
/// Окно микшера. Источник правды — неизменяемый граф в <see cref="GraphHistory"/>: любая правка интерфейса строит
/// новый GraphConfig, история рассылает его карточкам (<see cref="Sync"/>), хранилищу (автосохранение) и хосту
/// (движок). Состояние звука приходит обратно опросом <see cref="MixerHost.Status"/> и снимка движка без блокировок.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public static readonly string[] SourceColors = ["#4FA3FF", "#FF9A4D", "#3DCC91", "#B18CFF", "#F2C94C", "#FF6B9A", "#4DD0E1", "#A3D977"];

    private readonly GraphHistory _history;
    private readonly GraphStore _store;
    private readonly MixerHost? _host;
    private readonly Dictionary<string, NodeViewModel> _nodes = new();
    private readonly Dictionary<(string, string), WireViewModel> _wires = new();
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _toastTimer;
    private bool _layoutQueued;
    private NodeViewModel? _hover;
    private bool _interacting;
    private IReadOnlyList<EndpointInfo> _offlineDevices = [];

    /// <param name="offlineDevices">Устройства без хоста (тесты); null — прочитать при запуске.</param>
    public MainViewModel(GraphConfig graph, GraphStore store, MixerHost? host, string? startupMessage = null,
        IReadOnlyList<EndpointInfo>? offlineDevices = null)
    {
        _store = store;
        _host = host;
        _history = new GraphHistory(graph);
        _history.Changed += OnGraphChanged;
        _store.Saved += e => Application.Current?.Dispatcher.BeginInvoke(() => OnSaved(e));

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _toastTimer.Tick += (_, _) => { Toast = null; _toastTimer.Stop(); };

        Sync(graph);
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        MeterClock.Update += OnFrame;
        MeterClock.Start();

        if (offlineDevices is not null)
        {
            _offlineDevices = offlineDevices;
        }
        else if (host is null)
        {
            // без звука устройства всё равно нужны: подписи, проверка петель, палитра
            Task.Run(() => DeviceCatalog.ListAll()).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) _offlineDevices = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        if (startupMessage is not null) Notify(startupMessage);
        Poll();
    }

    public ObservableCollection<NodeViewModel> Nodes { get; } = new();
    public ObservableCollection<WireViewModel> Wires { get; } = new();
    public GraphConfig Graph => _history.Current;
    public MixerHost? Host => _host;
    public bool IsOffline => _host is null;

    /// <summary>Настройки программы (язык); null — без файла настроек (тесты).</summary>
    public Preferences? Preferences { get; init; }

    /// <summary>Пользователь попросил перезапустить программу (новый язык).</summary>
    public event Action? RestartRequested;

    internal void RequestRestart() => RestartRequested?.Invoke();

    /// <summary>Устройства для проверки проводов и меню; без хоста — прочитанные при запуске.</summary>
    public IReadOnlyList<EndpointInfo> Devices => _host?.Status.Devices is { Count: > 0 } d ? d : _offlineDevices;

    /// <summary>Кривые проводов надо перерисовать (узел сдвинулся, провод добавлен, сменился цвет).</summary>
    public event Action? WiresInvalidated;
    /// <summary>Вид должен открыть меню выбора устройства у карточки.</summary>
    public event Action<NodeViewModel>? DeviceMenuRequested;

    [ObservableProperty] private double _zoom = 1;
    [ObservableProperty] private double _canvasHeight = GraphLayout.MinHeight;
    [ObservableProperty] private object? _selected;
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private bool _toastIsError;
    [ObservableProperty] private PaletteViewModel? _palette;
    [ObservableProperty] private SettingsViewModel? _settings;

    public string ZoomText => Fmt.Percent(Zoom * 100) + "%";
    public bool IsEmpty => Nodes.Count == 0;
    public bool HasOverlay => Palette is not null || Settings is not null;

    partial void OnZoomChanged(double value) => OnPropertyChanged(nameof(ZoomText));
    partial void OnPaletteChanged(PaletteViewModel? value) => OnPropertyChanged(nameof(HasOverlay));
    partial void OnSettingsChanged(SettingsViewModel? value) => OnPropertyChanged(nameof(HasOverlay));

    // ── граф → карточки ──

    private void OnGraphChanged(GraphConfig g)
    {
        Sync(g);
        _store.Changed(g);
        _host?.Update(g);
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        SaveText = Strings.Header_Changed;
        SaveFailed = false;
        PollSoon();
    }

    private void Sync(GraphConfig g)
    {
        var seen = new HashSet<string>();
        foreach (var c in g.Nodes)
        {
            if (!seen.Add(c.Id)) continue;
            if (_nodes.TryGetValue(c.Id, out var vm))
            {
                if (vm.Config != c) vm.Load(c);
            }
            else
            {
                vm = new NodeViewModel(this, c);
                _nodes[c.Id] = vm;
                Nodes.Add(vm);
            }
        }
        foreach (var gone in _nodes.Values.Where(n => !seen.Contains(n.Id)).ToList())
        {
            _nodes.Remove(gone.Id);
            Nodes.Remove(gone);
            if (Selected == gone) Selected = null;
            if (_hover == gone) _hover = null;
            _picked.Remove(gone);
            if (Processing?.Node == gone) Processing = null;
        }

        var wseen = new HashSet<(string, string)>();
        foreach (var c in g.Wires)
        {
            var key = (c.From, c.To);
            if (!wseen.Add(key)) continue;
            if (!_nodes.TryGetValue(c.From, out var src) || !_nodes.TryGetValue(c.To, out var dst) || !src.HasOutPort || !dst.HasInPort || src == dst) continue;
            if (_wires.TryGetValue(key, out var w))
            {
                if (w.Config != c) w.Load(c);
            }
            else
            {
                w = new WireViewModel(c, src, dst);
                _wires[key] = w;
                Wires.Add(w);
            }
        }
        foreach (var gone in _wires.Values.Where(w => !wseen.Contains((w.From, w.To)) || !_nodes.ContainsKey(w.From) || !_nodes.ContainsKey(w.To)).ToList())
        {
            _wires.Remove((gone.From, gone.To));
            Wires.Remove(gone);
            if (Selected == gone) Selected = null;
        }

        CanvasHeight = GraphLayout.Height(g);
        OnPropertyChanged(nameof(IsEmpty));
        UpdateCounts();
        UpdateSilentBuses();
        Processing?.Load();
        GroupSelectedCommand.NotifyCanExecuteChanged();
        LayoutPills();
        ApplyDim();
        WiresInvalidated?.Invoke();
    }

    public NodeViewModel? FindNode(string id) => _nodes.GetValueOrDefault(id);
    public WireViewModel? FindWire(string from, string to) => _wires.GetValueOrDefault((from, to));

    internal void NodeMoved(NodeViewModel n)
    {
        if (_layoutQueued) return;
        _layoutQueued = true;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _layoutQueued = false;
            LayoutPills();
            WiresInvalidated?.Invoke();
        });
    }

    internal void InvalidateWires() => WiresInvalidated?.Invoke();

    /// <summary>
    /// Пилюля — в середине кривой. Если две пилюли наезжают друг на друга, вторая сдвигается вдоль своей кривой.
    /// </summary>
    private void LayoutPills()
    {
        var placed = new List<Point>();
        foreach (var w in Wires.OrderBy(w => w.Source.Y).ThenBy(w => w.Target.Y))
        {
            Point best = WireGeometry.At(w.A, w.B, 0.5);
            foreach (double t in new[] { 0.5, 0.38, 0.62, 0.3, 0.7, 0.44, 0.56 })
            {
                var p = WireGeometry.At(w.A, w.B, t);
                if (placed.All(q => Math.Abs(q.X - p.X) > 60 || Math.Abs(q.Y - p.Y) > 25))
                {
                    best = p;
                    break;
                }
            }
            placed.Add(best);
            w.PillX = best.X;
            w.PillY = best.Y;
        }
    }

    // ── правки ──

    public void Edit(Func<GraphConfig, GraphConfig> change, string? mergeKey = null)
    {
        var next = change(Graph);
        if (!ReferenceEquals(next, Graph)) _history.Apply(next, mergeKey);
    }

    public void EndMerge() => _history.EndMerge();

    internal void SetNodeGain(NodeViewModel n, double db) =>
        Edit(g => GraphEdits.UpdateNode(g, n.Id, c => c with { GainDb = (float)Fmt.SnapDb(db) }), "gain:" + n.Id);

    public void MoveNode(NodeViewModel n, double x, double y)
    {
        var (cx, cy) = GraphLayout.Clamp(n.Column, x, y);
        if (cx == n.Config.X && cy == n.Config.Y) return;
        Edit(g => GraphEdits.UpdateNode(g, n.Id, c => c with { X = cx, Y = cy }), "move:" + n.Id);
    }

    public void SetWireGain(WireViewModel w, double db, bool snapZero = true) =>
        Edit(g => GraphEdits.UpdateWire(g, w.From, w.To, c => c with { GainDb = (float)Fmt.SnapDb(db, snapZero) }), $"wire:{w.From}>{w.To}");

    public void ToggleWire(WireViewModel w) =>
        Edit(g => GraphEdits.UpdateWire(g, w.From, w.To, c => c with { Enabled = !c.Enabled }), $"pill:{w.From}>{w.To}");

    /// <summary>Двойной клик по пилюле: 0 дБ и включён (склеивается с первым кликом двойного).</summary>
    public void ResetWire(WireViewModel w)
    {
        Edit(g => GraphEdits.UpdateWire(g, w.From, w.To, c => c with { GainDb = 0, Enabled = true }), $"pill:{w.From}>{w.To}");
        EndMerge();
    }

    public void DeleteWire(WireViewModel w) => Edit(g => GraphEdits.Disconnect(g, w.From, w.To));

    public void DeleteNode(NodeViewModel n) => Edit(g => GraphEdits.RemoveNode(g, n.Id));

    /// <summary>Цепочка обработки узла: правка из боковой панели (ручки склеиваются в один шаг по ключу).</summary>
    public void SetChain(NodeViewModel n, Mixer.Audio.Dsp.ChainSettings chain, string? mergeKey = null) =>
        Edit(g => GraphEdits.UpdateNode(g, n.Id, c => c with { Chain = chain.Normalized() }), mergeKey is null ? null : $"chain:{n.Id}:{mergeKey}");

    public ConnectCheck CheckConnect(string a, string b) => ConnectionRules.Check(Graph, a, b, Devices);

    /// <summary>Соединить, если можно; иначе показать причину.</summary>
    public bool Connect(string a, string b)
    {
        var check = CheckConnect(a, b);
        if (!check.Ok)
        {
            if (check.Verdict != ConnectVerdict.Exists) Notify(check.Reason ?? Strings.Graph_WireNotCreated, error: true);
            return false;
        }
        Edit(g => GraphEdits.Connect(g, check.From, check.To));
        Select(FindWire(check.From, check.To));
        return true;
    }

    public void ToggleConnection(NodeViewModel source, NodeViewModel output)
    {
        if (FindWire(source.Id, output.Id) is { } w) DeleteWire(w);
        else Connect(source.Id, output.Id);
    }

    /// <summary>Перецепить конец провода. Дроп в пустоту — провод удаляется.</summary>
    public void Reroute(WireViewModel w, string newFrom, string newTo)
    {
        if (newFrom == w.From && newTo == w.To) return;
        var check = ConnectionRules.Check(GraphEdits.Disconnect(Graph, w.From, w.To), newFrom, newTo, Devices);
        if (!check.Ok)
        {
            Notify(check.Reason ?? Strings.Graph_WireNotMoved, error: true);
            return;
        }
        Edit(g => GraphEdits.Reroute(g, w.From, w.To, check.From, check.To));
        Select(FindWire(check.From, check.To));
    }

    public void ChangeDevice(NodeViewModel n, EndpointInfo ep)
    {
        Edit(g => GraphEdits.UpdateNode(g, n.Id, c =>
        {
            bool defaultName = HasDefaultName(c);
            return c with { EndpointId = ep.Id, EndpointName = ep.Name, DefaultRole = null, Name = defaultName ? Fmt.ShortDevice(ep.Name) : c.Name };
        }));
    }

    /// <summary>Узел следует за устройством по умолчанию Windows для роли.</summary>
    public void FollowDefault(NodeViewModel n, DefaultRole role)
    {
        Edit(g => GraphEdits.UpdateNode(g, n.Id, c =>
        {
            bool defaultName = HasDefaultName(c);
            return c with { DefaultRole = role, EndpointId = null, EndpointName = null, Name = defaultName ? Fmt.DefaultNodeName(c.Kind, role) : c.Name };
        }));
    }

    /// <summary>Имя узла дал микшер (по устройству или «по умолчанию»), а не пользователь: при смене устройства его можно сменить.</summary>
    private static bool HasDefaultName(NodeConfig c) => c.DefaultRole is { } r
        ? Fmt.IsDefaultNodeName(c.Name, c.Kind, r)
        : c.EndpointName is null || c.Name == Fmt.ShortDevice(c.EndpointName) || c.Name == c.EndpointName;

    public void SetColor(NodeViewModel n, string hex) => Edit(g => GraphEdits.UpdateNode(g, n.Id, c => c with { Color = hex }));

    /// <summary>Исключить exe из «Весь звук системы» или вернуть его туда.</summary>
    public void SetExcluded(NodeViewModel n, string exe, bool excluded) => Edit(g => GraphEdits.SetExcluded(g, n.Id, exe, excluded));

    /// <summary>Приложения со звуком на последнем такте хоста (кандидаты в исключения).</summary>
    public IReadOnlyList<string> PlayingApps => _host?.Status.PlayingApps ?? [];

    public void SetSink(string? endpointId) => Edit(g => GraphEdits.SetSink(g, endpointId));

    /// <summary>Добавить узел из палитры. atY — где на холсте открыли палитру (правый клик), иначе вниз колонки.</summary>
    public NodeViewModel AddNode(NodeConfig template, double? atY = null)
    {
        var g = Graph;
        var (x, y) = GraphLayout.NextSlot(g, template.Column, atY);
        string id = GraphEdits.NewNodeId(g);
        string? color = template.Color ?? (template.IsSource ? NextColor(g) : null);
        var node = template with { Id = id, X = x, Y = y, Color = color };
        Edit(c => GraphEdits.AddNode(c, node));
        var vm = _nodes[id];
        Select(vm);
        if (GraphBuilderTakesOver(node) && string.IsNullOrEmpty(g.SinkEndpoint))
            Notify(Fmt.F(Strings.Graph_TakeOverNeedsSinkFormat, node.Name));
        return vm;
    }

    private static bool GraphBuilderTakesOver(NodeConfig n) => Mixer.Core.Graph.GraphBuilder.TakesOver(n);

    /// <summary>Новая шина в средней колонке: «Шина 1», «Шина 2», …</summary>
    public NodeViewModel AddBus(double? atY = null)
    {
        Palette = null;
        Settings = null;
        return AddNode(new NodeConfig { Id = "", Kind = NodeKind.Bus, Name = NextBusName(Graph) }, atY);
    }

    private static string NextBusName(GraphConfig g)
    {
        var used = g.Nodes.Select(n => n.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (int i = 1; ; i++)
        {
            string name = Fmt.F(Strings.Bus_NameFormat, i);
            if (!used.Contains(name)) return name;
        }
    }

    // ── шины: сгруппировать и разгруппировать ──

    /// <summary>Выделенные узлы, которые можно сгруппировать (источники и шины), по порядку на холсте.</summary>
    public IReadOnlyList<NodeViewModel> Picked => _picked.Where(n => n.HasOutPort).OrderBy(n => n.Y).ToList();

    private bool CanGroup() => Picked.Count >= 2;

    [RelayCommand(CanExecute = nameof(CanGroup))]
    private void GroupSelected() => GroupNodes(Picked);

    private void GroupNodes(IReadOnlyList<NodeViewModel> members)
    {
        if (members.Count < 2)
        {
            Notify(Mixer.Core.Resources.CoreStrings.Group_NeedTwo);
            return;
        }
        var g = Graph;
        double atY = members.Average(m => m.Y) + GraphLayout.PortY;
        var (x, y) = GraphLayout.NextSlot(g, NodeColumn.Buses, atY);
        var bus = new NodeConfig { Id = GraphEdits.NewNodeId(g), Kind = NodeKind.Bus, Name = NextBusName(g), X = x, Y = y };
        var r = GraphEdits.GroupIntoBus(g, members.Select(m => m.Id).ToList(), bus);
        if (r.Error is not null)
        {
            Notify(Fmt.Capitalize(r.Error), error: true);
            return;
        }
        Edit(_ => r.Graph);
        Select(_nodes.GetValueOrDefault(bus.Id));
        Notify(r.Moved == 0 ? Fmt.F(Strings.Bus_GroupedNoOutputsFormat, bus.Name)
            : r.Skipped > 0 ? Fmt.F(Strings.Bus_GroupedSomeFormat, bus.Name, Fmt.Plural(r.Moved, Strings.Plural_Output), Fmt.Plural(r.Skipped, Strings.Plural_Output))
            : Fmt.F(Strings.Bus_GroupedFormat, bus.Name, Fmt.Plural(r.Moved, Strings.Plural_Output)));
    }

    // ── предложение сгруппировать после переноса схемы v1 ──

    private readonly Queue<IReadOnlyList<string>> _groupOffers = new();
    private IReadOnlyList<string>? _groupOffer;

    /// <summary>Текст предложения «Сгруппировать в шину?» (null — предлагать нечего).</summary>
    [ObservableProperty] private string? _groupOfferText;

    /// <summary>
    /// Схему перенесли с v1: предложить шины для источников с одинаковым набором выходов (v2, «Перенос с v1»).
    /// Шины сами не создаются: предложения идут по одному, каждое можно принять или отклонить.
    /// </summary>
    public void OfferGrouping()
    {
        _groupOffers.Clear();
        foreach (var g in GraphEdits.GroupSuggestions(Graph)) _groupOffers.Enqueue(g);
        NextGroupOffer();
    }

    private void NextGroupOffer()
    {
        _groupOffer = null;
        GroupOfferText = null;
        while (_groupOffers.TryDequeue(out var ids))
        {
            // граф могли поправить, пока висело прежнее предложение
            var members = ids.Select(id => _nodes.GetValueOrDefault(id)).ToList();
            if (members.Any(m => m is null)) continue;
            var outs = Graph.Wires.Where(w => w.From == ids[0] && w.Enabled).Select(w => _nodes.GetValueOrDefault(w.To))
                .Where(o => o is { IsOutput: true }).Select(o => o!.Name).ToList();
            _groupOffer = ids;
            GroupOfferText = Fmt.F(Strings.Migrate_GroupOfferFormat,
                Fmt.List(members.Select(m => Quote(m!.Name))), Fmt.List(outs.Select(Quote)));
            return;
        }
    }

    /// <summary>Имя в кавычках языка интерфейса: «…» или “…”.</summary>
    private static string Quote(string name) => Fmt.F(Mixer.Core.Resources.CoreStrings.Path_NameFormat, name);

    [RelayCommand]
    private void AcceptGroupOffer()
    {
        var ids = _groupOffer;
        var members = ids?.Select(id => _nodes.GetValueOrDefault(id)).OfType<NodeViewModel>().ToList();
        // узлы предложения могли удалить, пока оно висело: тогда группировать нечего
        if (members is not null && members.Count == ids!.Count) GroupNodes(members);
        NextGroupOffer();
    }

    [RelayCommand]
    private void DismissGroupOffer() => NextGroupOffer();

    public void Ungroup(NodeViewModel bus)
    {
        var r = GraphEdits.Ungroup(Graph, bus.Id);
        if (r.Error is not null)
        {
            Notify(Fmt.Capitalize(r.Error), error: true);
            return;
        }
        Edit(_ => r.Graph);
        string text = Fmt.F(Strings.Bus_UngroupedFormat, bus.Name, Fmt.Plural(r.Moved, Strings.Plural_Wire));
        if (r.Skipped > 0) text += " " + Fmt.F(Strings.Bus_UngroupKeptFormat, Fmt.Plural(r.Skipped, Strings.Plural_Wire));
        if (r.LostChain) text += " " + Strings.Bus_UngroupLostChain;
        Notify(text);
    }

    /// <summary>Шина без входов или без выходов: «ничего не слышно».</summary>
    private void UpdateSilentBuses()
    {
        foreach (var n in Nodes)
        {
            if (!n.IsBus)
            {
                n.IsSilent = false;
                continue;
            }
            bool hasIn = Wires.Any(w => w.Target == n), hasOut = Wires.Any(w => w.Source == n);
            n.IsSilent = !hasIn || !hasOut;
        }
    }

    // ── боковая панель обработки ──

    [ObservableProperty] private ProcessingViewModel? _processing;

    public void OpenProcessing(NodeViewModel n, string? block = null)
    {
        Select(n);
        if (Processing?.Node != n) Processing = new ProcessingViewModel(this, n);
        Processing.Focus(block);
    }

    [RelayCommand]
    private void CloseProcessing() => Processing = null;

    internal static string NextColor(GraphConfig g)
    {
        var used = g.Nodes.Where(n => n.IsSource).Select(n => n.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return SourceColors.FirstOrDefault(c => !used.Contains(c)) ?? SourceColors[g.Nodes.Count(n => n.IsSource) % SourceColors.Length];
    }

    // ── выбор и подсветка маршрутов ──

    private readonly HashSet<NodeViewModel> _picked = new();

    public void Select(object? item)
    {
        foreach (var p in _picked) p.IsSelected = false;
        _picked.Clear();
        if (Selected is WireViewModel ow) ow.IsSelected = false;
        Selected = item;
        if (item is NodeViewModel nn)
        {
            nn.IsSelected = true;
            _picked.Add(nn);
        }
        if (item is WireViewModel nw) nw.IsSelected = true;
        GroupSelectedCommand.NotifyCanExecuteChanged();
        WiresInvalidated?.Invoke();
    }

    /// <summary>Ctrl + клик: добавить узел к выделению или убрать из него (чтобы сгруппировать в шину).</summary>
    public void TogglePick(NodeViewModel n)
    {
        if (Selected is WireViewModel ow)
        {
            ow.IsSelected = false;
            Selected = null;
        }
        if (!_picked.Remove(n))
        {
            _picked.Add(n);
            n.IsSelected = true;
            Selected = n;
        }
        else
        {
            n.IsSelected = false;
            if (Selected == n) Selected = _picked.FirstOrDefault();
        }
        GroupSelectedCommand.NotifyCanExecuteChanged();
        WiresInvalidated?.Invoke();
    }

    /// <summary>Навели на узел: его провода и соседи яркие, остальное приглушено.</summary>
    public void SetHover(NodeViewModel? n)
    {
        if (_hover == n) return;
        _hover = n;
        ApplyDim();
    }

    /// <summary>Пока тянут провод, подсветку маршрутов ведёт перетаскивание.</summary>
    public bool Interacting
    {
        get => _interacting;
        set
        {
            _interacting = value;
            ApplyDim();
        }
    }

    private void ApplyDim()
    {
        if (_interacting) return;
        var h = _hover;
        var near = new HashSet<NodeViewModel>();
        if (h is not null)
        {
            near.Add(h);
            foreach (var w in Wires)
            {
                if (w.Source == h) near.Add(w.Target);
                if (w.Target == h) near.Add(w.Source);
            }
        }
        foreach (var n in Nodes) n.IsDimmed = h is not null && !near.Contains(n);
        foreach (var w in Wires) w.IsDimmed = h is not null && w.Source != h && w.Target != h;
        WiresInvalidated?.Invoke();
    }

    /// <summary>
    /// Тянут провод от порта узла: допустимые порты обведены, недоступные гаснут, запрещённые — красные.
    /// fromInput — тянут от входного порта (слева): ищем, откуда звук пойдёт в узел; иначе — куда пойдёт из него.
    /// </summary>
    public void BeginConnectPreview(NodeViewModel origin, bool fromInput, WireViewModel? moving = null)
    {
        Interacting = true;
        foreach (var n in Nodes)
        {
            n.PortReason = null;
            n.InPort = PortLook.None;
            n.OutPort = PortLook.None;
            if (n == origin)
            {
                if (fromInput) n.InPort = PortLook.Origin;
                else n.OutPort = PortLook.Origin;
                n.IsDimmed = false;
                continue;
            }
            bool can = fromInput ? n.HasOutPort : n.HasInPort;
            if (!can)
            {
                n.IsDimmed = true;
                continue;
            }
            n.IsDimmed = false;
            PortLook look;
            if (moving is not null && (fromInput ? moving.Source == n : moving.Target == n))
            {
                look = PortLook.Valid; // вернуть конец на место — можно
            }
            else
            {
                var check = fromInput ? CheckConnect(n.Id, origin.Id) : CheckConnect(origin.Id, n.Id);
                // проверка не должна видеть сам переносимый провод: он уйдёт с прежнего места
                if (moving is not null && check.Verdict is ConnectVerdict.Cycle or ConnectVerdict.TooDeep or ConnectVerdict.Feedback)
                {
                    var without = GraphEdits.Disconnect(Graph, moving.From, moving.To);
                    check = fromInput ? ConnectionRules.Check(without, n.Id, origin.Id, Devices) : ConnectionRules.Check(without, origin.Id, n.Id, Devices);
                }
                look = check.Verdict switch
                {
                    ConnectVerdict.Ok => PortLook.Valid,
                    ConnectVerdict.Exists => PortLook.Occupied,
                    _ => PortLook.Forbidden,
                };
                n.PortReason = check.Reason;
            }
            if (fromInput) n.OutPort = look;
            else n.InPort = look;
        }
        foreach (var w in Wires) w.IsDimmed = w != moving;
        WiresInvalidated?.Invoke();
    }

    public void EndConnectPreview()
    {
        foreach (var n in Nodes)
        {
            n.InPort = PortLook.None;
            n.OutPort = PortLook.None;
            n.PortReason = null;
        }
        Interacting = false;
    }

    // ── команды ──

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        _history.Undo();
        if (!_history.CanUndo) Notify(Strings.Cmd_NothingToUndo);
    }

    private bool CanUndo() => _history.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => _history.Redo();

    private bool CanRedo() => _history.CanRedo;

    [RelayCommand]
    private void AddSource() => OpenPalette(PaletteMode.Sources, null);

    [RelayCommand]
    private void AddOutput() => OpenPalette(PaletteMode.Outputs, null);

    [RelayCommand]
    private void AddBusNode() => AddBus();

    public void OpenPalette(PaletteMode mode, double? atY)
    {
        Settings = null;
        var catalog = _host is not null
            ? _host.QueryCatalogAsync()
            : Task.Run(() => MixerHost.ReadCatalog(null));
        Palette = new PaletteViewModel(this, mode, atY, catalog);
    }

    /// <summary>Свежий список устройств — в управляющем потоке хоста (COM не в потоке интерфейса).</summary>
    public Task<List<EndpointInfo>> ListDevicesAsync() =>
        _host is not null ? _host.RunAsync(DeviceCatalog.ListAll) : Task.Run(DeviceCatalog.ListAll);

    [RelayCommand]
    private void OpenSettings()
    {
        Palette = null;
        Settings = new SettingsViewModel(this);
    }

    [RelayCommand]
    public void CloseOverlay()
    {
        Palette = null;
        Settings = null;
    }

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(2.0, Math.Round(Zoom + 0.1, 2));

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(0.5, Math.Round(Zoom - 0.1, 2));

    [RelayCommand]
    private void ZoomReset() => Zoom = 1;

    [RelayCommand]
    private void DeleteSelected()
    {
        if (Selected is NodeViewModel n) DeleteNode(n);
        else if (Selected is WireViewModel w) DeleteWire(w);
    }

    [RelayCommand]
    private void RenameSelected()
    {
        if (Selected is NodeViewModel n) n.IsRenaming = true;
    }

    [RelayCommand]
    private void OpenVolumeMixer()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:apps-volume") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Notify(Fmt.F(Strings.Cmd_OpenWindowsMixerFailedFormat, ex.Message), error: true);
        }
    }

    internal void RequestDeviceMenu(NodeViewModel n) => DeviceMenuRequested?.Invoke(n);

    public void Notify(string text, bool error = false)
    {
        Toast = text;
        ToastIsError = error;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ── сохранение ──

    [ObservableProperty] private string _saveText = Strings.Header_Saved;
    [ObservableProperty] private bool _saveFailed;

    private void OnSaved(Exception? error)
    {
        if (error is null)
        {
            if (!_store.IsDirty) SaveText = Strings.Header_Saved;
            SaveFailed = false;
            return;
        }
        SaveText = Strings.Header_NotSaved;
        SaveFailed = true;
        Notify(Fmt.F(Strings.Header_SaveFailedFormat, error.Message), error: true);
    }

    public void Dispose()
    {
        _poll.Stop();
        _toastTimer.Stop();
        MeterClock.Update -= OnFrame;
        MeterClock.Stop();
    }
}
