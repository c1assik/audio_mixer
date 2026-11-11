using Mixer.Audio.Dsp;
using Mixer.Core.Config;

namespace Mixer.Core.Editing;

/// <summary>
/// Сцены (v2, «Сцены»): именованные снимки уровней — фейдеры и mute узлов, усиление и включение проводов, включение
/// блоков обработки. Структуру схемы сцена не меняет: узел или провод, которого нет в сцене (или уже нет в схеме),
/// при применении не трогается. Переключение плавное: <see cref="Interpolate"/> за <see cref="FadeMs"/>.
/// </summary>
public static class Scenes
{
    /// <summary>Длительность плавного переключения, мс (критерий приёмки: не больше 150 мс и без щелчков).</summary>
    public const int FadeMs = 150;

    public static string NewId(GraphConfig g)
    {
        var used = g.Scenes.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        for (int i = 1; ; i++)
            if (!used.Contains("s" + i)) return "s" + i;
    }

    /// <summary>Снимок текущих уровней схемы.</summary>
    public static SceneConfig Capture(GraphConfig g, string id, string name, string? hotkey = null) => new()
    {
        Id = id,
        Name = name,
        Hotkey = hotkey,
        Nodes = g.Nodes.GroupBy(n => n.Id).Select(x => x.First()).ToDictionary(n => n.Id, n => new SceneNode
        {
            GainDb = n.GainDb,
            Muted = n.Muted,
            Gate = n.Chain?.Gate.Enabled,
            Eq = n.Chain?.Eq.Enabled,
            Comp = n.Chain?.Comp.Enabled,
        }),
        Wires = g.Wires.Select(w => new SceneWire { From = w.From, To = w.To, GainDb = w.GainDb, Enabled = w.Enabled }).ToList(),
    };

    /// <summary>Схема с уровнями сцены (мгновенно). Активной становится эта сцена.</summary>
    public static GraphConfig Apply(GraphConfig g, SceneConfig s) => g with
    {
        ActiveScene = s.Id,
        Nodes = g.Nodes.Select(n => s.Nodes.TryGetValue(n.Id, out var sn) ? ApplyNode(n, sn) : n).ToList(),
        Wires = g.Wires.Select(w => s.Wires.FirstOrDefault(x => x.From == w.From && x.To == w.To) is { } sw
            ? w with { GainDb = sw.GainDb, Enabled = sw.Enabled }
            : w).ToList(),
    };

    private static NodeConfig ApplyNode(NodeConfig n, SceneNode s)
    {
        var chain = n.Chain;
        if (chain is not null)
            chain = chain with
            {
                Gate = s.Gate is { } gate ? chain.Gate with { Enabled = gate } : chain.Gate,
                Eq = s.Eq is { } eq ? chain.Eq with { Enabled = eq } : chain.Eq,
                Comp = s.Comp is { } comp ? chain.Comp with { Enabled = comp } : chain.Comp,
            };
        return n with { GainDb = s.GainDb, Muted = s.Muted, Chain = chain };
    }

    /// <summary>Уровни схемы совпадают со сценой (иначе её поправили руками — в шапке точка).</summary>
    public static bool Matches(GraphConfig g, SceneConfig s)
    {
        foreach (var n in g.Nodes)
        {
            if (!s.Nodes.TryGetValue(n.Id, out var sn)) continue;
            if (Math.Abs(n.GainDb - sn.GainDb) > 0.05f || n.Muted != sn.Muted) return false;
            if (n.Chain is { } c && (sn.Gate is { } gt && gt != c.Gate.Enabled || sn.Eq is { } eq && eq != c.Eq.Enabled || sn.Comp is { } cp && cp != c.Comp.Enabled))
                return false;
        }
        foreach (var w in g.Wires)
        {
            if (s.Wires.FirstOrDefault(x => x.From == w.From && x.To == w.To) is not { } sw) continue;
            if (Math.Abs(w.GainDb - sw.GainDb) > 0.05f || w.Enabled != sw.Enabled) return false;
        }
        return true;
    }

    /// <summary>
    /// Промежуточная схема плавного перехода from → to (t от 0 до 1). Усиления идут линейно в дБ. Узел, который
    /// глушится, плавно уходит до −60 дБ и только в конце получает mute; включаемый — получает звук сразу и плавно
    /// поднимается от −60 дБ. Так же провода. Блоки обработки переключаются в начале (движок сам делает 10 мс кроссфейд).
    /// Структура берётся из from: узлы и провода, которых нет в to, не меняются.
    /// </summary>
    public static GraphConfig Interpolate(GraphConfig from, GraphConfig to, double t)
    {
        if (t >= 1) return to;
        t = Math.Max(0, t);
        var toNodes = to.Nodes.GroupBy(n => n.Id).ToDictionary(x => x.Key, x => x.First());
        var toWires = to.Wires.GroupBy(w => (w.From, w.To)).ToDictionary(x => x.Key, x => x.First());
        return to with
        {
            Nodes = from.Nodes.Select(a =>
            {
                if (!toNodes.TryGetValue(a.Id, out var b)) return a;
                var (gain, muted) = Step(a.GainDb, !a.Muted, b.GainDb, !b.Muted, t);
                return b with { GainDb = gain, Muted = !muted, X = a.X, Y = a.Y };
            }).ToList(),
            Wires = from.Wires.Select(a =>
            {
                if (!toWires.TryGetValue((a.From, a.To), out var b)) return a;
                var (gain, on) = Step(a.GainDb, a.Enabled, b.GainDb, b.Enabled, t);
                return b with { GainDb = gain, Enabled = on };
            }).ToList(),
        };
    }

    /// <summary>Шаг уровня: (усиление, звучит ли) в момент t.</summary>
    private static (float Gain, bool On) Step(float a, bool aOn, float b, bool bOn, double t)
    {
        if (!aOn && !bOn) return (b, false); // не звучит ни до, ни после
        float from = aOn ? a : Db.Min, to = bOn ? b : Db.Min;
        float gain = (float)(from + (to - from) * t);
        return (MathF.Round(gain * 100) / 100, t < 1 && (aOn || t > 0) || bOn);
    }

    // ── правки ──

    public static GraphConfig Add(GraphConfig g, SceneConfig s) => g with { Scenes = [.. g.Scenes, s], ActiveScene = s.Id };

    /// <summary>«Сохранить»: текущие уровни в сцену (имя и клавиша остаются).</summary>
    public static GraphConfig Save(GraphConfig g, string id) => Update(g, id, s => Capture(g, s.Id, s.Name, s.Hotkey)) with { ActiveScene = id };

    public static GraphConfig Rename(GraphConfig g, string id, string name) =>
        string.IsNullOrWhiteSpace(name) ? g : Update(g, id, s => s with { Name = name.Trim() });

    /// <summary>Клавиша сцены (null — снять). Та же клавиша у другой сцены снимается.</summary>
    public static GraphConfig SetHotkey(GraphConfig g, string id, string? keys) => g with
    {
        Scenes = g.Scenes.Select(s => s.Id == id ? s with { Hotkey = keys } : keys is not null && string.Equals(s.Hotkey, keys, StringComparison.OrdinalIgnoreCase) ? s with { Hotkey = null } : s).ToList(),
        // одно сочетание — одно действие: клавиша узла с тем же сочетанием снимается
        Hotkeys = keys is null ? g.Hotkeys : g.Hotkeys.Where(h => !string.Equals(h.Keys, keys, StringComparison.OrdinalIgnoreCase)).ToList(),
    };

    public static GraphConfig Delete(GraphConfig g, string id) => g with
    {
        Scenes = g.Scenes.Where(s => s.Id != id).ToList(),
        ActiveScene = g.ActiveScene == id ? null : g.ActiveScene,
    };

    private static GraphConfig Update(GraphConfig g, string id, Func<SceneConfig, SceneConfig> change) =>
        g with { Scenes = g.Scenes.Select(s => s.Id == id ? change(s) : s).ToList() };
}
