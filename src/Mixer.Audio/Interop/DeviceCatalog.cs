using System.Runtime.InteropServices;

namespace Mixer.Audio.Interop;

/// <param name="IsDefault">Устройство по умолчанию для роли Console (и Multimedia: Windows держит их одинаковыми).</param>
public sealed record EndpointInfo(string Id, string Name, EDataFlow Flow, bool IsDefault)
{
    /// <summary>Устройство по умолчанию для связи (роль Communications): его берут Discord, Teams и звонки.</summary>
    public bool IsDefaultComms { get; init; }

    /// <summary>Входы Voicemeeter VAIO без запущенного Voicemeeter вешают Initialize (этап 0, результат 4).</summary>
    public bool IsVoicemeeter => Name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Перечисление эндпоинтов. Вызывать не из потока интерфейса.</summary>
public static class DeviceCatalog
{
    internal static IMMDeviceEnumerator CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(AudioConst.MMDeviceEnumeratorClsid, throwOnError: true)!;
        return (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
    }

    public static List<EndpointInfo> List(EDataFlow flow)
    {
        var enumerator = CreateEnumerator();
        try
        {
            string? defaultId = DefaultId(enumerator, flow, ERole.Console);
            string? commsId = DefaultId(enumerator, flow, ERole.Communications);

            AudioException.Check(enumerator.EnumAudioEndpoints(flow, AudioConst.DEVICE_STATE_ACTIVE, out var collection), "EnumAudioEndpoints");
            AudioException.Check(collection.GetCount(out uint count), "IMMDeviceCollection.GetCount");
            var result = new List<EndpointInfo>((int)count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) < 0) continue;
                try
                {
                    if (device.GetId(out string id) < 0) continue;
                    result.Add(new EndpointInfo(id, ReadFriendlyName(device), flow, id == defaultId) { IsDefaultComms = id == commsId });
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            Marshal.ReleaseComObject(collection);
            return result;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static string? DefaultId(IMMDeviceEnumerator enumerator, EDataFlow flow, ERole role)
    {
        if (enumerator.GetDefaultAudioEndpoint(flow, role, out var def) < 0) return null;
        try
        {
            return def.GetId(out string id) >= 0 ? id : null;
        }
        finally
        {
            Marshal.ReleaseComObject(def);
        }
    }

    public static List<EndpointInfo> ListAll()
    {
        var all = List(EDataFlow.Render);
        all.AddRange(List(EDataFlow.Capture));
        return all;
    }

    internal static string ReadFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(AudioConst.STGM_READ, out var store) < 0) return "?";
        try
        {
            var key = AudioConst.PKEY_Device_FriendlyName;
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
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>
    /// Формат микса устройства (GetMixFormat), без Initialize. null, если устройство недоступно.
    /// Для эндпоинтов Voicemeeter не вызывать без нужды.
    /// </summary>
    public static WaveFormat? TryGetMixFormat(string endpointId)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? client = null;
        try
        {
            enumerator = CreateEnumerator();
            if (enumerator.GetDevice(endpointId, out device) < 0) return null;
            Guid iid = typeof(IAudioClient).GUID;
            if (device.Activate(ref iid, AudioConst.CLSCTX_ALL, IntPtr.Zero, out client) < 0) return null;
            if (((IAudioClient)client).GetMixFormat(out IntPtr p) < 0) return null;
            try { return WaveFormat.FromPointer(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        finally
        {
            if (client is not null) Marshal.ReleaseComObject(client);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }
}
