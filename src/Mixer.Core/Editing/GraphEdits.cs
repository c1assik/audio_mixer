using Mixer.Core.Config;

namespace Mixer.Core.Editing;

/// <summary>Чистые правки графа: каждая возвращает новый GraphConfig, исходный не меняется.</summary>
public static class GraphEdits
{
    /// <summary>Свободный ID узла: n1, n2, …</summary>
    public static string NewNodeId(GraphConfig g)
    {
        var used = g.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        for (int i = 1; ; i++)
            if (!used.Contains("n" + i)) return "n" + i;
    }

    public static NodeConfig? Find(GraphConfig g, string id) => g.Nodes.FirstOrDefault(n => n.Id == id);

    public static WireConfig? FindWire(GraphConfig g, string from, string to) => g.Wires.FirstOrDefault(w => w.From == from && w.To == to);

    public static GraphConfig AddNode(GraphConfig g, NodeConfig node) => g with { Nodes = [.. g.Nodes, node] };

    /// <summary>Удалить узел вместе с его проводами и горячими клавишами.</summary>
    public static GraphConfig RemoveNode(GraphConfig g, string id) => g with
    {
        Nodes = g.Nodes.Where(n => n.Id != id).ToList(),
        Wires = g.Wires.Where(w => w.From != id && w.To != id).ToList(),
        Hotkeys = g.Hotkeys.Where(h => h.Target != id).ToList(),
        MainMic = g.MainMic == id ? null : g.MainMic,
    };

    /// <summary>
    /// Горячая клавиша действия над узлом (keys = null — снять). Одно сочетание — одно действие: такая же клавиша у
    /// другого действия или у сцены снимается.
    /// </summary>
    public static GraphConfig SetHotkey(GraphConfig g, HotkeyAction action, string target, string? keys)
    {
        bool Same(string? k) => keys is not null && string.Equals(k, keys, StringComparison.OrdinalIgnoreCase);
        var list = g.Hotkeys.Where(h => !(h.Action == action && h.Target == target) && !Same(h.Keys)).ToList();
        if (keys is not null) list.Add(new HotkeyConfig { Action = action, Target = target, Keys = keys });
        return g with
        {
            Hotkeys = list,
            Scenes = g.Scenes.Select(s => Same(s.Hotkey) ? s with { Hotkey = null } : s).ToList(),
        };
    }

    /// <summary>Главный микрофон (значок в трее, mute из меню трея).</summary>
    public static GraphConfig SetMainMic(GraphConfig g, string? id) => g.MainMic == id ? g : g with { MainMic = id };

    /// <summary>Добавить exe в исключения узла «Весь звук системы» или убрать его оттуда (имя нормализуется, дубли не пишутся).</summary>
    public static GraphConfig SetExcluded(GraphConfig g, string id, string exe, bool excluded)
    {
        if (string.IsNullOrWhiteSpace(exe)) return g;
        string name = Sys.ProcessSnapshot.NormalizeExe(exe);
        return UpdateNode(g, id, n =>
        {
            var list = (n.Exclude ?? []).Where(e => !Sys.ProcessSnapshot.NormalizeExe(e).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (excluded) list.Add(name);
            list.Sort(StringComparer.Ordinal);
            return n with { Exclude = list.Count == 0 ? null : list };
        });
    }

    /// <summary>Назначить выход кабеля микрофоном приложению или снять назначение (micFor; имя нормализуется, дубли не пишутся).</summary>
    public static GraphConfig SetMicFor(GraphConfig g, string id, string exe, bool on)
    {
        if (string.IsNullOrWhiteSpace(exe)) return g;
        string name = Sys.ProcessSnapshot.NormalizeExe(exe);
        return UpdateNode(g, id, n =>
        {
            var list = (n.MicFor ?? []).Where(e => !Sys.ProcessSnapshot.NormalizeExe(e).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (on) list.Add(name);
            return n with { MicFor = list.Count == 0 ? null : list };
        });
    }

    /// <summary>
    /// Связать узел устройства с другим концом кабеля, которого нет в таблице сопоставления. Роль — по типу узла: выход
    /// пишет в кабель (виртуальный микрофон), вход читает из него (виртуальный вход).
    /// </summary>
    public static GraphConfig LinkCable(GraphConfig g, string id, Mixer.Audio.Interop.EndpointInfo partner) =>
        UpdateNode(g, id, n => n.Kind is not (NodeKind.DeviceInput or NodeKind.DeviceOutput) ? n : n with
        {
            Cable = new CableLink
            {
                PairId = "manual:" + partner.Id.ToLowerInvariant(),
                Role = n.Kind == NodeKind.DeviceOutput ? CableRole.VirtualMic : CableRole.VirtualInput,
                PartnerId = partner.Id,
                PartnerName = partner.Name,
            },
        });

    /// <summary>Отвязать кабель, связанный вручную; назначения микрофона снимаются вместе с ним.</summary>
    public static GraphConfig UnlinkCable(GraphConfig g, string id) => UpdateNode(g, id, n => n with { Cable = null, MicFor = null });

    public static GraphConfig UpdateNode(GraphConfig g, string id, Func<NodeConfig, NodeConfig> change)
    {
        bool hit = false;
        var nodes = g.Nodes.Select(n =>
        {
            if (n.Id != id) return n;
            hit = true;
            return change(n);
        }).ToList();
        return hit ? g with { Nodes = nodes } : g;
    }

    /// <summary>Провод от источника к выходу. Проверка допустимости — <see cref="ConnectionRules"/>; дубль не добавляется.</summary>
    public static GraphConfig Connect(GraphConfig g, string from, string to, float gainDb = 0, bool enabled = true)
    {
        if (FindWire(g, from, to) is not null) return g;
        return g with { Wires = [.. g.Wires, new WireConfig { From = from, To = to, GainDb = gainDb, Enabled = enabled }] };
    }

    public static GraphConfig Disconnect(GraphConfig g, string from, string to) =>
        FindWire(g, from, to) is null ? g : g with { Wires = g.Wires.Where(w => !(w.From == from && w.To == to)).ToList() };

    public static GraphConfig UpdateWire(GraphConfig g, string from, string to, Func<WireConfig, WireConfig> change)
    {
        bool hit = false;
        var wires = g.Wires.Select(w =>
        {
            if (w.From != from || w.To != to) return w;
            hit = true;
            return change(w);
        }).ToList();
        return hit ? g with { Wires = wires } : g;
    }

    /// <summary>Перецепить конец провода на другой узел, сохранив усиление и включённость (на том же месте в списке).</summary>
    public static GraphConfig Reroute(GraphConfig g, string from, string to, string newFrom, string newTo)
    {
        if (from == newFrom && to == newTo) return g;
        if (FindWire(g, from, to) is null || FindWire(g, newFrom, newTo) is not null) return g;
        return UpdateWire(g, from, to, w => w with { From = newFrom, To = newTo });
    }

    /// <summary>
    /// «Сгруппировать в шину»: новая шина получает провода из выделенных узлов и общие выходы, у которых провода от
    /// всех выделенных включены; прямые провода в эти выходы убираются. Итоговые уровни не меняются: усиление старого
    /// провода раскладывается на «источник → шина» и «шина → выход» (у каждого источника своё, у каждого выхода своё).
    /// Выход, для которого такой раскладки нет (уровни расходятся по-разному), остаётся на прямых проводах.
    /// Шина без общих выходов тоже создаётся: она только получает входы, звук не меняется.
    /// </summary>
    /// <summary>
    /// Что предложить сгруппировать после переноса схемы v1 (v2, «Перенос с v1»): источники с одинаковым набором выходов.
    /// Берутся включённые провода прямо в выходы; источник, у которого уже есть провод в шину, не предлагается.
    /// Группа — от двух источников и от двух общих выходов: с одним выходом шина проводов не убавит.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> GroupSuggestions(GraphConfig g)
    {
        var byId = g.Nodes.GroupBy(n => n.Id).ToDictionary(x => x.Key, x => x.First());
        var groups = new Dictionary<string, List<string>>();
        var order = new List<string>();
        foreach (var n in g.Nodes.Where(n => n.IsSource).OrderBy(n => n.Y))
        {
            var wires = g.Wires.Where(w => w.From == n.Id && w.Enabled).ToList();
            if (wires.Any(w => byId.GetValueOrDefault(w.To)?.IsBus == true)) continue;
            var outs = wires.Where(w => byId.GetValueOrDefault(w.To)?.IsOutput == true).Select(w => w.To).Distinct().Order(StringComparer.Ordinal).ToList();
            if (outs.Count < 2) continue;
            string key = string.Join('\u0001', outs);
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
                order.Add(key);
            }
            list.Add(n.Id);
        }
        return order.Select(k => groups[k]).Where(l => l.Count >= 2).Select(l => (IReadOnlyList<string>)l).ToList();
    }

    public static GroupResult GroupIntoBus(GraphConfig g, IReadOnlyList<string> members, NodeConfig bus)
    {
        var nodes = members.Distinct().Select(id => Find(g, id)).ToList();
        if (nodes.Count < 2 || nodes.Any(n => n is null || !n.HasOutPort))
            return new GroupResult(g, CoreStrings.Group_NeedTwo);
        var ms = nodes.Select(n => n!).OrderBy(n => n.Y).ToList();

        Dictionary<string, float> Outs(NodeConfig m) =>
            g.Wires.Where(w => w.From == m.Id && w.Enabled).ToDictionary(w => w.To, w => w.GainDb);
        var outs = ms.ToDictionary(m => m.Id, Outs);
        var common = outs[ms[0].Id].Keys.Where(t => ms.All(m => outs[m.Id].ContainsKey(t)))
            .Select(t => Find(g, t)!).Where(t => t is not null).OrderBy(t => t.Y).Select(t => t.Id).ToList();

        // g(m, c) = a(m) + b(c): берём выходы по порядку, пока раскладка сходится
        var a = new Dictionary<string, float>();
        var b = new Dictionary<string, float>();
        foreach (string c in common)
        {
            if (b.Count == 0)
            {
                foreach (var m in ms) a[m.Id] = outs[m.Id][c];
                b[c] = 0;
                continue;
            }
            float bc = outs[ms[0].Id][c] - a[ms[0].Id];
            if (ms.All(m => Math.Abs(outs[m.Id][c] - (a[m.Id] + bc)) < 0.05f)) b[c] = bc;
        }
        if (b.Count == 0)
            foreach (var m in ms) a[m.Id] = 0;
        // провода из шины — не громче 0 дБ: шина сама по себе ничего не поднимает
        float shift = b.Count == 0 ? 0 : b.Values.Max();
        var moved = b.Keys.ToHashSet();

        var wires = g.Wires.Where(w => !(moved.Contains(w.To) && a.ContainsKey(w.From))).ToList();
        foreach (var m in ms) wires.Add(new WireConfig { From = m.Id, To = bus.Id, GainDb = Round(a[m.Id] + shift) });
        foreach (string c in common.Where(moved.Contains)) wires.Add(new WireConfig { From = bus.Id, To = c, GainDb = Round(b[c] - shift) });
        var next = g with { Nodes = [.. g.Nodes, bus], Wires = wires };

        var paths = new Graph.GraphPaths(next);
        if (next.Nodes.Where(n => n.IsBus).Any(n => paths.BusesAbove(n.Id) + paths.BusesBelowDepth(n.Id) - 1 > Graph.GraphPaths.MaxBusDepth))
            return new GroupResult(g, Loc.F(CoreStrings.Connect_TooDeepFormat, Graph.GraphPaths.MaxBusDepth));
        return new GroupResult(next, null, moved.Count, common.Count - moved.Count);
    }

    /// <summary>
    /// «Разгруппировать»: шина убирается, каждый её вход ведётся прямо в каждый её выход с суммой усилений
    /// (вход + фейдер шины + выход). Выключенный провод или заглушённая шина дают выключенный провод. Если прямой
    /// провод уже есть, он остаётся как был. Обработка шины пропадает (это видно в результате).
    /// </summary>
    public static GroupResult Ungroup(GraphConfig g, string busId)
    {
        var bus = Find(g, busId);
        if (bus is null || !bus.IsBus) return new GroupResult(g, CoreStrings.Connect_NodeNotFound);
        var ins = g.Wires.Where(w => w.To == busId).ToList();
        var outs = g.Wires.Where(w => w.From == busId).ToList();
        var wires = g.Wires.Where(w => w.From != busId && w.To != busId).ToList();
        int added = 0, kept = 0;
        foreach (var i in ins)
        {
            foreach (var o in outs)
            {
                if (wires.Any(w => w.From == i.From && w.To == o.To))
                {
                    kept++;
                    continue;
                }
                float gain = Math.Clamp(i.GainDb + bus.GainDb + o.GainDb, Mixer.Audio.Dsp.Db.Min, Mixer.Audio.Dsp.Db.Max);
                wires.Add(new WireConfig { From = i.From, To = o.To, GainDb = Round(gain), Enabled = i.Enabled && o.Enabled && !bus.Muted });
                added++;
            }
        }
        var next = g with { Nodes = g.Nodes.Where(n => n.Id != busId).ToList(), Wires = wires };
        return new GroupResult(next, null, added, kept) { LostChain = bus.HasChain };
    }

    private static float Round(float db) => MathF.Round(db * 10) / 10;

    public static GraphConfig SetSink(GraphConfig g, string? endpointId) =>
        string.Equals(g.SinkEndpoint, endpointId, StringComparison.OrdinalIgnoreCase) ? g : g with { SinkEndpoint = endpointId };
}

/// <summary>Итог «Сгруппировать» / «Разгруппировать».</summary>
/// <param name="Error">Почему правка не сделана (граф тогда прежний).</param>
/// <param name="Moved">Сгруппировать: выходов перенесено на шину. Разгруппировать: прямых проводов создано.</param>
/// <param name="Skipped">Сгруппировать: общих выходов осталось на прямых проводах. Разгруппировать: прямых проводов уже было.</param>
public sealed record GroupResult(GraphConfig Graph, string? Error, int Moved = 0, int Skipped = 0)
{
    /// <summary>У убранной шины была включена обработка.</summary>
    public bool LostChain { get; init; }
}
