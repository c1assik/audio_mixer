using System.Diagnostics;
using System.Runtime.InteropServices;
using Mixer.Probe.Interop;

namespace Mixer.Probe.Sys;

/// <summary>
/// Подписка на новые аудиосессии эндпоинтов (IAudioSessionNotification.OnSessionCreated).
/// Это альтернатива опросу раз в секунду: новая сессия видна сразу, а не на следующем тике.
/// </summary>
public sealed class SessionWatcher : IDisposable
{
    private readonly List<(IAudioSessionManager2 Manager, Handler Handler)> _subs = new();

    /// <summary>PID процесса новой сессии и момент уведомления (Stopwatch). Вызывается из потока аудиослужбы.</summary>
    public event Action<uint, long>? SessionCreated;

    public List<string> Errors { get; } = new();

    public static SessionWatcher Subscribe(IEnumerable<Endpoint> endpoints)
    {
        var w = new SessionWatcher();
        var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)!;
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
        foreach (var ep in endpoints)
        {
            try
            {
                AudioException.Check(enumerator.GetDevice(ep.Id, out var device), "GetDevice");
                Guid iid = typeof(IAudioSessionManager2).GUID;
                AudioException.Check(device.Activate(ref iid, AudioConst.CLSCTX_ALL, IntPtr.Zero, out object mgrObj), "Activate(IAudioSessionManager2)");
                var manager = (IAudioSessionManager2)mgrObj;
                // Без предварительного GetSessionEnumerator уведомления не приходят (известная особенность API).
                AudioException.Check(manager.GetSessionEnumerator(out var sessions), "GetSessionEnumerator");
                Marshal.ReleaseComObject(sessions);
                var handler = new Handler(w);
                AudioException.Check(manager.RegisterSessionNotification(handler), "RegisterSessionNotification");
                w._subs.Add((manager, handler));
            }
            catch (Exception ex) when (ex is AudioException or COMException or InvalidCastException)
            {
                w.Errors.Add($"{ep.Name}: {ex.Message}");
            }
        }
        return w;
    }

    public void Dispose()
    {
        foreach (var (m, h) in _subs) m.UnregisterSessionNotification(h);
        _subs.Clear();
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class Handler(SessionWatcher owner) : IAudioSessionNotification, IAgileObject
    {
        public int OnSessionCreated(object newSession)
        {
            long now = Stopwatch.GetTimestamp();
            try
            {
                if (newSession is IAudioSessionControl2 ctl && ctl.GetProcessId(out uint pid) >= 0)
                    owner.SessionCreated?.Invoke(pid, now);
            }
            catch (COMException)
            {
                // сессия могла исчезнуть сразу после создания
            }
            return 0;
        }
    }
}

/// <summary>Защита машины разработчика: потоки на эндпоинтах Voicemeeter VAIO без запущенного Voicemeeter вешали Audiosrv.</summary>
public static class Safety
{
    public static bool IsVoicemeeter(string name) =>
        name.Contains("VAIO", StringComparison.OrdinalIgnoreCase) || name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase);

    public static Endpoint Check(Endpoint e)
    {
        if (IsVoicemeeter(e.Name))
            throw new UsageException($"«{e.Name}» — эндпоинт Voicemeeter. Потоки на нём без запущенного Voicemeeter вешали службу Windows Audio, " +
                                     "в замерах этапа 0 он не используется.");
        return e;
    }

    /// <summary>Без потока на «устройстве по умолчанию» нельзя, если это VAIO: так звук уходил в мёртвый эндпоинт.</summary>
    public static void CheckDefaultRender(IReadOnlyList<Endpoint> render)
    {
        var def = render.FirstOrDefault(e => e.IsDefault);
        if (def is not null && IsVoicemeeter(def.Name))
            throw new UsageException($"Устройство воспроизведения по умолчанию — «{def.Name}». Генератор с automatic routing ушёл бы в него; " +
                                     "смените устройство по умолчанию.");
    }
}
