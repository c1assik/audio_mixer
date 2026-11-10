using System.Runtime.InteropServices;
using Mixer.Probe.Interop;

namespace Mixer.Probe.Sys;

public sealed record Endpoint(string Id, string Name, EDataFlow Flow, bool IsDefault)
{
    /// <summary>Путь интерфейса MMDevice, который принимает IAudioPolicyConfig.</summary>
    public string InterfacePath => Devices.ToInterfacePath(Id, Flow);
}

public sealed record SessionInfo(uint Pid, string EndpointId, string EndpointName, int State, float Volume = -1, bool Muted = false)
{
    /// <summary>Громкость приложения в микшере громкости Windows для этого устройства.</summary>
    public string VolumeText => Volume < 0 ? "?" : Muted ? "mute" : Volume <= 0 ? "−∞ дБ" : $"{20 * Math.Log10(Volume):F1} дБ ({Volume * 100:F0}%)";

    public bool IsActive => State == AudioConst.AudioSessionStateActive;

    public string StateName => State switch
    {
        AudioConst.AudioSessionStateActive => "активна",
        AudioConst.AudioSessionStateInactive => "неактивна",
        AudioConst.AudioSessionStateExpired => "истекла",
        _ => State.ToString(),
    };
}

public static class Devices
{
    private const string MmDevApiPrefix = @"\\?\SWD#MMDEVAPI#";
    private const string RenderInterfaceClass = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string CaptureInterfaceClass = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";
    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly PropertyKey FriendlyNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    public static string ToInterfacePath(string endpointId, EDataFlow flow) =>
        MmDevApiPrefix + endpointId + (flow == EDataFlow.Capture ? CaptureInterfaceClass : RenderInterfaceClass);

    /// <summary>Обратное преобразование: из пути интерфейса в ID эндпоинта.</summary>
    public static string? EndpointIdFromInterfacePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        int start = path.IndexOf("MMDEVAPI#", StringComparison.OrdinalIgnoreCase);
        int end = path.LastIndexOf('#');
        if (start < 0 || end <= start) return path;
        start += "MMDEVAPI#".Length;
        return path[start..end];
    }

    private static IMMDeviceEnumerator CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid, throwOnError: true)!;
        return (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
    }

    public static List<Endpoint> List(EDataFlow flow)
    {
        var enumerator = CreateEnumerator();
        string? defaultId = null;
        if (enumerator.GetDefaultAudioEndpoint(flow, ERole.Console, out var def) >= 0)
            def.GetId(out defaultId);

        AudioException.Check(enumerator.EnumAudioEndpoints(flow, AudioConst.DEVICE_STATE_ACTIVE, out var collection), "EnumAudioEndpoints");
        AudioException.Check(collection.GetCount(out uint count), "IMMDeviceCollection.GetCount");
        var result = new List<Endpoint>((int)count);
        for (uint i = 0; i < count; i++)
        {
            if (collection.Item(i, out var device) < 0) continue;
            if (device.GetId(out string id) < 0) continue;
            result.Add(new Endpoint(id, ReadFriendlyName(device), flow, id == defaultId));
        }
        return result;
    }

    private static string ReadFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(AudioConst.STGM_READ, out var store) < 0) return "?";
        var key = FriendlyNameKey;
        if (store.GetValue(ref key, out var value) < 0) return "?";
        try
        {
            return value.vt == AudioConst.VT_LPWSTR ? Marshal.PtrToStringUni(value.pointerValue) ?? "?" : "?";
        }
        finally
        {
            NativeMethods.PropVariantClear(ref value);
        }
    }

    public static IAudioClient ActivateAudioClient(string endpointId)
    {
        var enumerator = CreateEnumerator();
        AudioException.Check(enumerator.GetDevice(endpointId, out var device), "IMMDeviceEnumerator.GetDevice");
        Guid iid = typeof(IAudioClient).GUID;
        AudioException.Check(device.Activate(ref iid, AudioConst.CLSCTX_ALL, IntPtr.Zero, out object client), "IMMDevice.Activate(IAudioClient)");
        return (IAudioClient)client;
    }

    /// <summary>Все аудиосессии на заданных эндпоинтах, включая неактивные.</summary>
    public static List<SessionInfo> Sessions(IEnumerable<Endpoint> endpoints)
    {
        var enumerator = CreateEnumerator();
        var result = new List<SessionInfo>();
        foreach (var ep in endpoints)
        {
            try
            {
                AudioException.Check(enumerator.GetDevice(ep.Id, out var device), "GetDevice");
                Guid iid = typeof(IAudioSessionManager2).GUID;
                AudioException.Check(device.Activate(ref iid, AudioConst.CLSCTX_ALL, IntPtr.Zero, out object mgrObj), "Activate(IAudioSessionManager2)");
                var manager = (IAudioSessionManager2)mgrObj;
                AudioException.Check(manager.GetSessionEnumerator(out var sessions), "GetSessionEnumerator");
                sessions.GetCount(out int n);
                for (int i = 0; i < n; i++)
                {
                    if (sessions.GetSession(i, out object ctlObj) < 0) continue;
                    if (ctlObj is not IAudioSessionControl2 ctl) continue;
                    if (ctl.IsSystemSoundsSession() == 0) continue; // S_OK = системные звуки
                    ctl.GetProcessId(out uint pid);
                    ctl.GetState(out int state);
                    float volume = -1;
                    bool muted = false;
                    if (ctlObj is ISimpleAudioVolume sv && sv.GetMasterVolume(out float v) >= 0)
                    {
                        volume = v;
                        sv.GetMute(out muted);
                    }
                    result.Add(new SessionInfo(pid, ep.Id, ep.Name, state, volume, muted));
                }
            }
            catch (Exception ex) when (ex is AudioException or COMException or InvalidCastException)
            {
                // эндпоинт мог пропасть между перечислением и активацией
            }
        }
        return result;
    }

    public static Endpoint Find(IReadOnlyList<Endpoint> endpoints, string query)
    {
        var byId = endpoints.FirstOrDefault(e => e.Id.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;

        var matches = endpoints.Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];

        var all = string.Join("\n", endpoints.Select(e => $"    {e.Name}  {e.Id}"));
        if (matches.Count == 0)
            throw new UsageException($"Устройство «{query}» не найдено. Доступны:\n{all}");
        throw new UsageException($"«{query}» подходит к нескольким устройствам, уточните имя или укажите ID:\n" +
                                 string.Join("\n", matches.Select(e => $"    {e.Name}  {e.Id}")));
    }

    public static string NameOf(IReadOnlyList<Endpoint> endpoints, string? interfacePathOrId)
    {
        string? id = EndpointIdFromInterfacePath(interfacePathOrId);
        if (id is null) return "по умолчанию";
        return endpoints.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name ?? id;
    }
}

public sealed class UsageException(string message) : Exception(message);
