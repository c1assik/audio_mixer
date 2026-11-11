using Mixer.Audio.Interop;
using Mixer.Core.Apps;
using Mixer.Core.Config;
using Mixer.Core.Sys;

namespace Mixer.Core.Cables;

/// <summary>
/// Проверки узлов-кабелей (v2, «Виртуальные кабели»): в кабель-микрофон пишет кто-то ещё, кроме микшера; у концов
/// кабеля разная частота (частая причина треска); приложение, которому назначен кабель, читает другой микрофон
/// (внутри приложения выбрано устройство, а не «По умолчанию»).
/// </summary>
public static class CableChecks
{
    /// <param name="world">Процессы и сессии воспроизведения (null — нечего проверять по сессиям).</param>
    /// <param name="captureSessions">Сессии устройств записи (null — не читались: на схеме нет назначенных микрофонов).</param>
    /// <param name="rateOf">Частота микса устройства по ID (null — неизвестна).</param>
    /// <returns>Предупреждения по ID узла.</returns>
    public static Dictionary<string, List<string>> Check(GraphConfig g, IReadOnlyList<EndpointInfo> devices, CableTable table,
        AppSnapshot? world, IReadOnlyList<AudioSessionInfo>? captureSessions, Func<string, int?> rateOf)
    {
        var result = new Dictionary<string, List<string>>();
        void Add(string id, string text)
        {
            if (!result.TryGetValue(id, out var list)) result[id] = list = [];
            list.Add(text);
        }

        var self = world?.Processes.Tree([world.Processes.SelfPid]) ?? [];
        foreach (var n in g.Nodes)
        {
            if (table.LinkOf(n) is not { } link || table.PairOf(n, devices) is not { } pair) continue;

            // разная частота концов
            if (pair is { Input: { } i, Output: { } o } && rateOf(i.Id) is { } ri && rateOf(o.Id) is { } ro && ri != ro)
                Add(n.Id, Loc.F(CoreStrings.Cable_RateMismatchFormat, i.Name, Khz(ri), o.Name, Khz(ro)));

            if (link.Role != CableRole.VirtualMic || n.Kind != NodeKind.DeviceOutput) continue;

            // кто ещё пишет в кабель-микрофон
            if (world is not null)
            {
                var inputs = pair.Inputs.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var others = world.Sessions
                    .Where(s => s.IsActive && s.Pid != 0 && !self.Contains(s.Pid) && inputs.Contains(s.EndpointId))
                    .Select(s => ProcessSnapshot.NormalizeExe(world.Processes.ExeOf(s.Pid)))
                    .Where(e => e != "?")
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
                if (others.Count > 0)
                    Add(n.Id, Loc.F(CoreStrings.Cable_OthersWriteFormat, string.Join(", ", others), pair.Input?.Name ?? n.Name));
            }

            // приложение с назначенным кабелем читает другой микрофон
            if (world is null || captureSessions is null || pair.Output is null || n.MicFor is not { Count: > 0 } apps) continue;
            foreach (string app in apps)
            {
                string exe = ProcessSnapshot.NormalizeExe(app);
                var tree = world.TreeOf(exe);
                if (tree.Count == 0) continue;
                var reads = captureSessions.Where(s => s.IsActive && tree.Contains(s.Pid)).ToList();
                if (reads.Count == 0 || reads.Any(s => string.Equals(s.EndpointId, pair.Output.Id, StringComparison.OrdinalIgnoreCase))) continue;
                string other = devices.FirstOrDefault(d => string.Equals(d.Id, reads[0].EndpointId, StringComparison.OrdinalIgnoreCase))?.Name ?? reads[0].EndpointId;
                Add(n.Id, Loc.F(CoreStrings.Cable_AppReadsOtherFormat, AppNames.Friendly(exe), other, pair.Output.Name));
            }
        }
        return result;
    }

    private static string Khz(int rate) => (rate / 1000.0).ToString(rate % 1000 == 0 ? "0" : "0.#", System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>Понятные имена частых приложений для подписей («obs64.exe» → «OBS Studio»).</summary>
public static class AppNames
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["obs64.exe"] = "OBS Studio",
        ["obs32.exe"] = "OBS Studio",
        ["obs.exe"] = "OBS Studio",
        ["discord.exe"] = "Discord",
        ["discordptb.exe"] = "Discord PTB",
        ["discordcanary.exe"] = "Discord Canary",
        ["zoom.exe"] = "Zoom",
        ["ms-teams.exe"] = "Microsoft Teams",
        ["teams.exe"] = "Microsoft Teams",
        ["skype.exe"] = "Skype",
        ["telegram.exe"] = "Telegram",
        ["slack.exe"] = "Slack",
        ["streamlabs obs.exe"] = "Streamlabs",
        ["streamlabs desktop.exe"] = "Streamlabs",
    };

    /// <summary>Приложения для звонков: помощник назначает им кабель «Звонок».</summary>
    public static readonly string[] CallApps = ["discord.exe", "zoom.exe", "ms-teams.exe", "teams.exe", "skype.exe", "telegram.exe", "slack.exe"];

    /// <summary>Приложения для стрима: им помощник назначает кабель «Стрим».</summary>
    public static readonly string[] StreamApps = ["obs64.exe", "streamlabs obs.exe", "streamlabs desktop.exe"];

    public static string Friendly(string exe)
    {
        string e = ProcessSnapshot.NormalizeExe(exe);
        return Known.TryGetValue(e, out var name) ? name : e;
    }
}
