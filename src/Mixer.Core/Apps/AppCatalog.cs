using Mixer.Audio.Interop;
using Mixer.Core.Sys;

namespace Mixer.Core.Apps;

/// <summary>Приложение в палитре «Добавить источник».</summary>
/// <param name="Exe">Нормализованное имя exe («chrome.exe»).</param>
/// <param name="Name">Описание из exe («Google Chrome»).</param>
/// <param name="Path">Путь к exe (для иконки), если удалось прочитать.</param>
/// <param name="Processes">Сколько процессов этого exe запущено.</param>
/// <param name="HasSession">Есть аудиосессия (приложение обращалось к звуку).</param>
/// <param name="Playing">Активная сессия: звук идёт сейчас.</param>
public sealed record AppEntry(string Exe, string Name, string? Path, int Processes, bool HasSession, bool Playing);

/// <summary>Всё, что нужно палитре: устройства, приложения и доступность захвата.</summary>
public sealed record SourceCatalog(
    IReadOnlyList<EndpointInfo> Devices,
    IReadOnlyList<AppEntry> Apps,
    string? ProcessLoopbackError,
    bool VoicemeeterRunning);

public static class AppCatalog
{
    /// <summary>Системные процессы, которые не нужны в списке приложений.</summary>
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "svchost.exe", "audiodg.exe", "csrss.exe", "dwm.exe", "winlogon.exe", "lsass.exe",
        "textinputhost.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe", "searchhost.exe",
        "applicationframehost.exe", "lockapp.exe", "sihost.exe", "ctfmon.exe", "runtimebroker.exe",
    };

    /// <summary>
    /// Сначала приложения с аудиосессией (играющие — выше), ниже — остальные с окном. Микшер и его дочерние процессы
    /// не показываются: свой процесс захватывать нельзя.
    /// </summary>
    public static List<AppEntry> Build(ProcessSnapshot procs, IReadOnlyList<AudioSessionInfo> sessions, IReadOnlySet<uint> windowOwners,
        Func<uint, string?> pathOf, Func<string, string?, string> describe)
    {
        var self = procs.Tree([procs.SelfPid]);
        var sessionExe = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); // exe → играет ли
        foreach (var s in sessions)
        {
            if (s.Pid == 0 || self.Contains(s.Pid) || !procs.ByPid.TryGetValue(s.Pid, out var p)) continue;
            string exe = ProcessSnapshot.NormalizeExe(p.Exe);
            sessionExe[exe] = sessionExe.GetValueOrDefault(exe) || s.IsActive;
        }
        var windowExe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (uint pid in windowOwners)
            if (!self.Contains(pid) && procs.ByPid.TryGetValue(pid, out var p))
                windowExe.Add(ProcessSnapshot.NormalizeExe(p.Exe));

        var result = new List<AppEntry>();
        foreach (string exe in sessionExe.Keys.Concat(windowExe).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Hidden.Contains(exe)) continue;
            var all = procs.All(exe).Where(p => !self.Contains(p.Pid)).ToList();
            if (all.Count == 0) continue;
            string? path = null;
            foreach (var p in procs.Roots(exe).Concat(all))
                if ((path = pathOf(p.Pid)) is not null) break;
            bool has = sessionExe.TryGetValue(exe, out bool playing);
            result.Add(new AppEntry(exe, describe(exe, path), path, all.Count, has, has && playing));
        }
        return result
            .OrderByDescending(a => a.Playing)
            .ThenByDescending(a => a.HasSession)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
