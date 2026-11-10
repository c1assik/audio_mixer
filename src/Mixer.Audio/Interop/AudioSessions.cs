using System.Runtime.InteropServices;

namespace Mixer.Audio.Interop;

public enum AudioSessionState { Inactive = 0, Active = 1, Expired = 2 }

/// <summary>Аудиосессия процесса на устройстве воспроизведения.</summary>
public sealed record AudioSessionInfo(uint Pid, string EndpointId, AudioSessionState State)
{
    public bool IsActive => State == AudioSessionState.Active;
}

/// <summary>Аудиосессии через IAudioSessionManager2. Вызывать не из потока интерфейса и не из аудиопотоков.</summary>
public static class AudioSessions
{
    /// <summary>Сессии на заданных эндпоинтах воспроизведения, включая неактивные. Системные звуки пропускаются.</summary>
    public static List<AudioSessionInfo> List(IEnumerable<string> renderEndpointIds)
    {
        var result = new List<AudioSessionInfo>();
        var enumerator = DeviceCatalog.CreateEnumerator();
        try
        {
            foreach (string epId in renderEndpointIds)
            {
                IMMDevice? device = null;
                object? mgrObj = null;
                IAudioSessionEnumerator? sessions = null;
                try
                {
                    if (enumerator.GetDevice(epId, out device) < 0) continue;
                    Guid iid = typeof(IAudioSessionManager2).GUID;
                    if (device.Activate(ref iid, AudioConst.CLSCTX_ALL, IntPtr.Zero, out mgrObj) < 0) continue;
                    if (((IAudioSessionManager2)mgrObj).GetSessionEnumerator(out sessions) < 0) continue;
                    if (sessions.GetCount(out int n) < 0) continue;
                    for (int i = 0; i < n; i++)
                    {
                        if (sessions.GetSession(i, out object ctlObj) < 0) continue;
                        try
                        {
                            if (ctlObj is not IAudioSessionControl2 ctl) continue;
                            if (ctl.IsSystemSoundsSession() == 0) continue; // S_OK — системные звуки
                            if (ctl.GetProcessId(out uint pid) < 0) continue; // AUDCLNT_S_NO_SINGLE_PROCESS тоже >= 0
                            if (ctl.GetState(out int state) < 0) continue;
                            result.Add(new AudioSessionInfo(pid, epId, (AudioSessionState)state));
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(ctlObj);
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
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
        return result;
    }
}
