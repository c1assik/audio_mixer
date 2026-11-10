using Mixer.Audio.Interop;

namespace Mixer.Core.Host;

/// <summary>
/// Защита от дребезга: срабатывает, когда события стихли на <paramref name="quiet"/> секунд, но не позже
/// <paramref name="maxWait"/> секунд после первого (поток событий не должен откладывать реакцию бесконечно).
/// USB-гарнитура при подключении присылает пачку событий: два эндпоинта, состояния, смена устройства по умолчанию.
/// </summary>
public sealed class Debounce(double quiet, double maxWait)
{
    private double? _first;
    private double _last;

    public bool Pending => _first is not null;

    public void Hit(double now)
    {
        _first ??= now;
        _last = now;
    }

    /// <summary>Когда сработает (секунды тех же часов), null — событий нет.</summary>
    public double? Due => _first is { } f ? Math.Min(_last + quiet, f + maxWait) : null;

    /// <summary>Пора — сбросить и вернуть true.</summary>
    public bool TryFire(double now)
    {
        if (Due is not { } due || now < due) return false;
        _first = null;
        return true;
    }
}

/// <summary>Что изменилось в списке устройств — для журнала и для решения, пересобирать ли граф.</summary>
public static class DeviceDiff
{
    /// <summary>Описание изменений или null, если список тот же (с точностью до ID, имён и устройств по умолчанию).</summary>
    public static string? Describe(IReadOnlyList<EndpointInfo> before, IReadOnlyList<EndpointInfo> after)
    {
        var parts = new List<string>();
        var old = before.GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var now = after.GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var d in after)
            if (!old.ContainsKey(d.Id)) parts.Add($"connected \"{d.Name}\"");
        foreach (var d in before)
            if (!now.ContainsKey(d.Id)) parts.Add($"disconnected \"{d.Name}\"");
        foreach (var d in after)
            if (old.TryGetValue(d.Id, out var o) && o.Name != d.Name) parts.Add($"\"{o.Name}\" is now called \"{d.Name}\"");

        foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
        {
            string what = flow == EDataFlow.Render ? "playback" : "recording";
            Default(before, after, flow, d => d.IsDefault, $"default {what} device", parts);
            Default(before, after, flow, d => d.IsDefaultComms, $"default communications {what} device", parts);
        }
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static void Default(IReadOnlyList<EndpointInfo> before, IReadOnlyList<EndpointInfo> after, EDataFlow flow,
        Func<EndpointInfo, bool> pick, string what, List<string> parts)
    {
        var a = before.FirstOrDefault(d => d.Flow == flow && pick(d));
        var b = after.FirstOrDefault(d => d.Flow == flow && pick(d));
        if (string.Equals(a?.Id, b?.Id, StringComparison.OrdinalIgnoreCase)) return;
        parts.Add(b is null ? $"{what}: none" : $"{what}: \"{b.Name}\"");
    }

    /// <summary>ID, которых коснулось изменение: появились, пропали, переименованы или стали (перестали быть) по умолчанию.</summary>
    public static HashSet<string> Touched(IReadOnlyList<EndpointInfo> before, IReadOnlyList<EndpointInfo> after)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var old = before.GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var now = after.GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var (id, d) in now)
            if (!old.TryGetValue(id, out var o) || o != d) set.Add(id);
        foreach (string id in old.Keys)
            if (!now.ContainsKey(id)) set.Add(id);
        return set;
    }
}
