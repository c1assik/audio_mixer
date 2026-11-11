using System.Runtime.InteropServices;

namespace Mixer.Audio.Interop;

/// <summary>Сессия приложения: громкость и индикатор. Абстракция — ради тестов без Windows Audio.</summary>
public interface IAppSession : IDisposable
{
    uint Pid { get; }
    string EndpointId { get; }
    (float Level, bool Muted)? Volume { get; }
    /// <summary>HRESULT.</summary>
    int SetVolume(float level, bool muted);
    (float L, float R) Peaks();
}

/// <summary>
/// Сессия приложения на устройстве воспроизведения: громкость (ISimpleAudioVolume) и пиковый индикатор
/// (IAudioMeterInformation). Нужна прямому маршруту: звук идёт мимо микшера, и уровень с mute задаются громкостью
/// сессии, а индикатор берётся у Windows. Вызывать из одного MTA-потока (управляющего), не из потока интерфейса.
/// </summary>
public sealed class AppSession : IAppSession
{
    private object? _control;
    private readonly ISimpleAudioVolume? _volume;
    private readonly IAudioMeterInformation? _meter;
    private readonly float[] _peaks = new float[8];
    private uint _channels;

    public uint Pid { get; }
    public string EndpointId { get; }
    /// <summary>ID экземпляра сессии (IAudioSessionControl2::GetSessionInstanceIdentifier): по нему сессии сопоставляются между опросами.</summary>
    public string InstanceId { get; }

    private AppSession(object control, uint pid, string endpointId, string instanceId)
    {
        _control = control;
        Pid = pid;
        EndpointId = endpointId;
        InstanceId = instanceId;
        _volume = control as ISimpleAudioVolume;
        _meter = control as IAudioMeterInformation;
        if (_meter is not null && _meter.GetMeteringChannelCount(out uint ch) >= 0) _channels = Math.Min(ch, (uint)_peaks.Length);
    }

    public bool CanSetVolume => _volume is not null;

    /// <summary>Текущая громкость и mute (null — не прочитать).</summary>
    public (float Level, bool Muted)? Volume
    {
        get
        {
            if (_volume is null || _volume.GetMasterVolume(out float v) < 0 || _volume.GetMute(out bool m) < 0) return null;
            return (v, m);
        }
    }

    /// <summary>Поставить громкость и mute, только если они отличаются (лишний Set будит чужие регуляторы громкости). HRESULT.</summary>
    public int SetVolume(float level, bool muted)
    {
        if (_volume is null) return unchecked((int)0x80004002); // E_NOINTERFACE
        level = Math.Clamp(level, 0f, 1f);
        int hr = 0;
        if (_volume.GetMasterVolume(out float cur) < 0 || Math.Abs(cur - level) > 1e-4f) hr = _volume.SetMasterVolume(level, IntPtr.Zero);
        if (hr < 0) return hr;
        if (_volume.GetMute(out bool m) < 0 || m != muted) hr = _volume.SetMute(muted, IntPtr.Zero);
        return hr;
    }

    /// <summary>Пики L и R сессии с прошлого опроса (моно — в оба канала). Без индикатора — нули.</summary>
    public (float L, float R) Peaks()
    {
        if (_meter is null) return (0, 0);
        if (_channels >= 2 && _meter.GetChannelsPeakValues(_channels, _peaks) >= 0) return (_peaks[0], _peaks[1]);
        return _meter.GetPeakValue(out float p) >= 0 ? (p, p) : (0, 0);
    }

    /// <summary>
    /// Сессии на устройстве воспроизведения, чьи процессы проходят фильтр, включая неактивные (громкость надо
    /// поставить до первого звука). Системные звуки и истёкшие сессии пропускаются.
    /// </summary>
    public static List<AppSession> Open(string endpointId, Func<uint, bool> pidFilter)
    {
        var result = new List<AppSession>();
        var enumerator = DeviceCatalog.CreateEnumerator();
        IMMDevice? device = null;
        object? mgrObj = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            if (enumerator.GetDevice(endpointId, out device) < 0) return result;
            Guid iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, AudioConst.CLSCTX_ALL, IntPtr.Zero, out mgrObj) < 0) return result;
            if (((IAudioSessionManager2)mgrObj).GetSessionEnumerator(out sessions) < 0) return result;
            if (sessions.GetCount(out int n) < 0) return result;
            for (int i = 0; i < n; i++)
            {
                if (sessions.GetSession(i, out object ctlObj) < 0) continue;
                bool keep = false;
                try
                {
                    if (ctlObj is not IAudioSessionControl2 ctl) continue;
                    if (ctl.IsSystemSoundsSession() == 0) continue;
                    if (ctl.GetProcessId(out uint pid) < 0 || !pidFilter(pid)) continue;
                    if (ctl.GetState(out int state) < 0 || state == (int)AudioSessionState.Expired) continue;
                    string instance = "";
                    if (ctl.GetSessionInstanceIdentifier(out IntPtr p) >= 0 && p != IntPtr.Zero)
                    {
                        instance = Marshal.PtrToStringUni(p) ?? "";
                        Marshal.FreeCoTaskMem(p);
                    }
                    result.Add(new AppSession(ctlObj, pid, endpointId, instance));
                    keep = true;
                }
                finally
                {
                    if (!keep) Marshal.ReleaseComObject(ctlObj);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // эндпоинт мог пропасть между перечислением и активацией
        }
        finally
        {
            if (sessions is not null) Marshal.ReleaseComObject(sessions);
            if (mgrObj is not null) Marshal.ReleaseComObject(mgrObj);
            if (device is not null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
        return result;
    }

    public void Dispose()
    {
        if (_control is null) return;
        Marshal.ReleaseComObject(_control);
        _control = null;
    }
}

/// <summary>
/// Подписка на новые аудиосессии устройств воспроизведения (IAudioSessionNotification.OnSessionCreated). Сессия
/// создаётся при Initialize приложения, до первого звука, поэтому захват успевает открыться почти без потерь
/// (этап 0 v2: ~35 мс вместо 260–560 мс при опросе раз в секунду). Подписываться из MTA-потока, после
/// GetSessionEnumerator — иначе уведомления не приходят.
/// </summary>
public sealed class SessionNotifier : IDisposable
{
    private readonly List<(IAudioSessionManager2 Manager, Handler Handler, string EndpointId)> _subs = new();

    /// <summary>PID процесса новой сессии и эндпоинт. Вызывается из потока аудиослужбы: только отметить и вернуться.</summary>
    public event Action<uint, string>? Created;

    /// <summary>Эндпоинты, на которые подписаться не удалось, с причиной.</summary>
    public List<string> Errors { get; } = new();

    public IReadOnlyList<string> Endpoints => _subs.Select(s => s.EndpointId).ToList();

    public static SessionNotifier Subscribe(IEnumerable<EndpointInfo> renderEndpoints)
    {
        var w = new SessionNotifier();
        var enumerator = DeviceCatalog.CreateEnumerator();
        try
        {
            foreach (var ep in renderEndpoints)
            {
                IMMDevice? device = null;
                object? mgrObj = null;
                try
                {
                    AudioException.Check(enumerator.GetDevice(ep.Id, out device), "GetDevice");
                    Guid iid = typeof(IAudioSessionManager2).GUID;
                    AudioException.Check(device.Activate(ref iid, AudioConst.CLSCTX_ALL, IntPtr.Zero, out mgrObj), "Activate(IAudioSessionManager2)");
                    var manager = (IAudioSessionManager2)mgrObj;
                    // без предварительного GetSessionEnumerator уведомления не приходят (этап 0)
                    AudioException.Check(manager.GetSessionEnumerator(out var sessions), "GetSessionEnumerator");
                    Marshal.ReleaseComObject(sessions);
                    var handler = new Handler(w, ep.Id);
                    AudioException.Check(manager.RegisterSessionNotification(handler), "RegisterSessionNotification");
                    w._subs.Add((manager, handler, ep.Id));
                    mgrObj = null; // живёт в подписке
                }
                catch (Exception ex) when (ex is AudioException or COMException or InvalidCastException)
                {
                    w.Errors.Add($"{ep.Name}: {ex.Message}");
                }
                finally
                {
                    if (mgrObj is not null) Marshal.ReleaseComObject(mgrObj);
                    if (device is not null) Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
        return w;
    }

    public void Dispose()
    {
        foreach (var (m, h, _) in _subs)
        {
            try
            {
                m.UnregisterSessionNotification(h);
            }
            catch (COMException)
            {
                // устройство пропало вместе с подпиской
            }
            Marshal.ReleaseComObject(m);
        }
        _subs.Clear();
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class Handler(SessionNotifier owner, string endpointId) : IAudioSessionNotification, IAgileObject
    {
        public int OnSessionCreated(object newSession)
        {
            try
            {
                if (newSession is IAudioSessionControl2 ctl && ctl.GetProcessId(out uint pid) >= 0)
                    owner.Created?.Invoke(pid, endpointId);
            }
            catch (COMException)
            {
                // сессия могла исчезнуть сразу после создания
            }
            return 0;
        }
    }
}
