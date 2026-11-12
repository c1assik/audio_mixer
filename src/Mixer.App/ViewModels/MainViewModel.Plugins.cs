using System.IO;
using Mixer.App.Ui;
using Mixer.Core.Config;
using Mixer.Core.Editing;
using Mixer.Plugins.Catalog;

namespace Mixer.App.ViewModels;

/// <summary>Куда добавить плагин из выбора: группа узла, или слот, который заменяется.</summary>
public sealed record PluginTarget(NodeViewModel Node, PluginGroup Group, string? ReplaceId = null);

/// <summary>Слоты плагинов (VST, «Интерфейс»): правки идут через историю, как любая правка схемы.</summary>
public sealed partial class MainViewModel
{
    private PluginCatalog? _offlineCatalog;
    private readonly Dictionary<(string Node, string Slot), PluginSlotViewModel> _slotVms = new();

    /// <summary>Модель слота — одна на слот: у неё подписки на окно редактора и параметры хоста.</summary>
    internal PluginSlotViewModel SlotVm(NodeViewModel n, PluginConfig p)
    {
        if (_slotVms.TryGetValue((n.Id, p.Id), out var vm) && vm.Config.ClassId == p.ClassId)
        {
            vm.Load(p);
            return vm;
        }
        return _slotVms[(n.Id, p.Id)] = new PluginSlotViewModel(this, n, p);
    }

    public void OpenPluginEditor(NodeViewModel n, string slotId)
    {
        if (n.Config.Plugins?.FirstOrDefault(x => x.Id == slotId) is not { } p) return;
        if (_host?.Plugins.HostOf(n.Id, slotId)?.Info is { HasEditor: false }) OpenProcessing(n, "fx");
        var vm = SlotVm(n, p);
        if (!vm.ParamsOpen) vm.OpenCommand.Execute(null);
    }

    /// <summary>Каталог для тестов вместо кэша в %LocalAppData%.</summary>
    public PluginCatalog? PluginCatalogOverride { get; init; }
    /// <summary>Папки для тестов вместо стандартных и своих из настроек.</summary>
    public Func<IEnumerable<string>>? PluginFoldersOverride { get; init; }

    public PluginCatalog Catalog => PluginCatalogOverride ?? _host?.Catalog ?? (_offlineCatalog ??= new PluginCatalog(new PluginScanner()));

    public IEnumerable<string> PluginFolders =>
        PluginFoldersOverride?.Invoke() ?? PluginFiles.StandardFolders.Concat(Preferences?.PluginFolders ?? []);

    /// <summary>Папка graph.json: в ней plugins\ (состояния слотов) и presets\ (пресеты слотов).</summary>
    public string DataFolder => Path.GetDirectoryName(Path.GetFullPath(_store.Path))!;

    public string StateFile(string nodeId, string slotId) => Path.Combine(DataFolder, PluginEdits.StatePath(nodeId, slotId));

    public void OpenPluginPicker(PluginTarget target)
    {
        Settings = null;
        Palette = new PaletteViewModel(this, target);
    }

    /// <summary>Плагин из выбора: новый слот (свой ID и файл состояния) в конец группы или на место заменяемого.</summary>
    public void AddPlugin(PluginTarget t, CatalogEntry e)
    {
        var node = GraphEdits.Find(Graph, t.Node.Id);
        if (node is null) return;
        string id = PluginEdits.NewSlotId(node, x => File.Exists(StateFile(node.Id, x)));
        var p = new PluginConfig
        {
            Id = id,
            Group = t.Group,
            ClassId = e.Class.ClassId,
            Name = e.Class.Name,
            Vendor = e.Class.Vendor,
            Path = e.Module.Path,
            State = PluginEdits.StatePath(node.Id, id),
        };
        if (t.ReplaceId is { } old) Edit(g => PluginEdits.Replace(g, node.Id, old, p));
        else if (!PluginEdits.HasRoom(Graph, node, t.Group))
        {
            Notify(Strings.Plugin_NoRoom, error: true);
            return;
        }
        else Edit(g => PluginEdits.Add(g, node.Id, p));
        Preferences?.MarkPluginUsed(e.Class.ClassId);
        OpenProcessing(t.Node, "fx");
    }

    public void RemovePlugin(NodeViewModel n, string slotId) => Edit(g => PluginEdits.Remove(g, n.Id, slotId));

    public void SetPluginEnabled(NodeViewModel n, string slotId, bool on) =>
        Edit(g => PluginEdits.Update(g, n.Id, slotId, p => p with { Enabled = on }));

    public void SetEvenBlocks(NodeViewModel n, string slotId, bool on) =>
        Edit(g => PluginEdits.Update(g, n.Id, slotId, p => p with { EvenBlocks = on }));

    public void MovePlugin(NodeViewModel n, string slotId, PluginGroup group, int index) =>
        Edit(g => PluginEdits.Move(g, n.Id, slotId, group, index));

    /// <summary>Окно редактора закрыли: его место — в слот (одним шагом истории на окно), состояние — в файл.</summary>
    internal void EditorClosed(NodeViewModel n, string slotId, PluginEditorBounds bounds)
    {
        Edit(g => PluginEdits.Update(g, n.Id, slotId, p => p with { Editor = bounds }), $"editor:{n.Id}:{slotId}");
        EndMerge();
        _ = _host?.Plugins.SaveStateNowAsync(n.Id, slotId);
    }

    public void RestartPlugin(NodeViewModel n, string slotId)
    {
        if (_host is null) return;
        if (!_host.Plugins.Restart(n.Id, slotId)) _host.Plugins.RetryMissing();
        PollSoon();
    }

    // ── пресеты слота: %AppData%\Mixer\presets\<classId>\<имя>.state ──

    private string PresetFolder(PluginConfig p) => Path.Combine(DataFolder, "presets", p.ClassId ?? "unknown");

    public IReadOnlyList<string> PluginPresets(PluginConfig p)
    {
        try
        {
            var dir = PresetFolder(p);
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.state").Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.CurrentCultureIgnoreCase).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public async Task SavePluginPreset(NodeViewModel n, PluginConfig p, string name)
    {
        name = string.Concat(name.Trim().Split(Path.GetInvalidFileNameChars()));
        if (name.Length == 0) return;
        byte[]? state = _host is null ? null : await _host.Plugins.SaveStateNowAsync(n.Id, p.Id);
        if (state is null)
        {
            Notify(Strings.Plugin_NotRunning, error: true);
            return;
        }
        try
        {
            Mixer.Core.Plugins.PluginRack.WriteAtomic(Path.Combine(PresetFolder(p), name + ".state"), state);
            Notify(Fmt.F(Strings.Fx_PresetSavedFormat, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify(Fmt.F(Strings.Fx_PresetSaveFailedFormat, ex.Message), error: true);
        }
    }

    public async Task LoadPluginPreset(NodeViewModel n, PluginConfig p, string name)
    {
        byte[] state;
        try
        {
            state = await File.ReadAllBytesAsync(Path.Combine(PresetFolder(p), name + ".state"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify(Fmt.F(Strings.Plugin_PresetLoadFailedFormat, ex.Message), error: true);
            return;
        }
        if (_host is null || !await _host.Plugins.SetStateAsync(n.Id, p.Id, state))
        {
            Notify(Strings.Plugin_NotRunning, error: true);
            return;
        }
        Notify(Fmt.F(Strings.Fx_PresetAppliedFormat, name, p.Name));
    }
}
