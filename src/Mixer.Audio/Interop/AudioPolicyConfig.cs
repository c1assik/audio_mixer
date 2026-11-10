using System.Runtime.InteropServices;
using Mixer.Audio.Resources;

namespace Mixer.Audio.Interop;

/// <summary>
/// Недокументированная фабрика Windows.Media.Internal.AudioPolicyConfig: устройство вывода для отдельного приложения
/// (перенесено из Mixer.Probe, проверено на этапе 0). Раскладка как в EarTrumpet: IInspectable (6 слотов), 19 заглушек, затем
/// SetPersistedDefaultAudioEndpoint (25), GetPersistedDefaultAudioEndpoint (26).
/// Методы вызываются напрямую через vtable, потому что .NET 8 не маршалит HSTRING и IInspectable.
/// Привязка хранится за путём exe, а не за PID; Set/Get принимают только PID живого процесса с аудиоконтекстом,
/// иначе — E_INVALIDARG.
/// </summary>
public sealed unsafe class AudioPolicyConfig : IDisposable
{
    private const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";
    private const int SlotSet = 25;
    private const int SlotGet = 26;

    private static readonly (string Name, Guid Iid)[] Variants =
    [
        ("21H2+ (ab3d4648)", new Guid("ab3d4648-e242-459f-b02f-541c70306324")),
        ("downlevel (2a59116d)", new Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258")),
    ];

    private IntPtr _factory;

    public string Variant { get; }

    private AudioPolicyConfig(IntPtr factory, string variant)
    {
        _factory = factory;
        Variant = variant;
    }

    /// <summary>Пробует оба известных IID. Исключение — API недоступен в этой сборке Windows.</summary>
    public static AudioPolicyConfig Create()
    {
        AudioException.Check(NativeMethods.WindowsCreateString(ClassName, (uint)ClassName.Length, out IntPtr hClass), "WindowsCreateString");
        try
        {
            var errors = new List<string>();
            foreach (var (name, iid) in Variants)
            {
                Guid g = iid;
                int hr = NativeMethods.RoGetActivationFactory(hClass, ref g, out IntPtr factory);
                if (hr >= 0 && factory != IntPtr.Zero) return new AudioPolicyConfig(factory, name);
                errors.Add($"{name}: {Hr.Describe(hr)}");
            }
            throw new AudioException(Loc.F(AudioStrings.Policy_UnavailableFormat, string.Join("; ", errors)));
        }
        finally
        {
            NativeMethods.WindowsDeleteString(hClass);
        }
    }

    /// <summary>endpointId = null сбрасывает привязку на «по умолчанию».</summary>
    public int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, string? endpointId)
    {
        string? path = endpointId is null ? null : EndpointPath.ToInterfacePath(endpointId, flow);
        IntPtr hs = IntPtr.Zero;
        if (!string.IsNullOrEmpty(path))
            AudioException.Check(NativeMethods.WindowsCreateString(path, (uint)path.Length, out hs), "WindowsCreateString");
        try
        {
            var fn = (delegate* unmanaged<IntPtr, uint, int, int, IntPtr, int>)Slot(SlotSet);
            return fn(_factory, processId, (int)flow, (int)role, hs);
        }
        finally
        {
            if (hs != IntPtr.Zero) NativeMethods.WindowsDeleteString(hs);
        }
    }

    /// <summary>endpointId = null — привязки нет (приложение играет в устройство по умолчанию).</summary>
    public int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out string? endpointId)
    {
        IntPtr hs = IntPtr.Zero;
        var fn = (delegate* unmanaged<IntPtr, uint, int, int, IntPtr*, int>)Slot(SlotGet);
        int hr = fn(_factory, processId, (int)flow, (int)role, &hs);
        endpointId = null;
        if (hs != IntPtr.Zero)
        {
            IntPtr raw = NativeMethods.WindowsGetStringRawBuffer(hs, out uint len);
            endpointId = EndpointPath.FromInterfacePath(Marshal.PtrToStringUni(raw, (int)len));
            NativeMethods.WindowsDeleteString(hs);
        }
        return hr;
    }

    private IntPtr Slot(int index)
    {
        ObjectDisposedException.ThrowIf(_factory == IntPtr.Zero, this);
        IntPtr* vtbl = *(IntPtr**)_factory;
        return vtbl[index];
    }

    public void Dispose()
    {
        if (_factory != IntPtr.Zero)
        {
            Marshal.Release(_factory);
            _factory = IntPtr.Zero;
        }
    }
}

/// <summary>ID эндпоинта ↔ путь интерфейса MMDevice, который принимает IAudioPolicyConfig.</summary>
public static class EndpointPath
{
    private const string MmDevApiPrefix = @"\\?\SWD#MMDEVAPI#";
    private const string RenderInterfaceClass = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string CaptureInterfaceClass = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";

    public static string ToInterfacePath(string endpointId, EDataFlow flow) =>
        MmDevApiPrefix + endpointId + (flow == EDataFlow.Capture ? CaptureInterfaceClass : RenderInterfaceClass);

    /// <summary>Обратное преобразование. Пустая строка — null; строка без префикса MMDEVAPI возвращается как есть.</summary>
    public static string? FromInterfacePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        int start = path.IndexOf("MMDEVAPI#", StringComparison.OrdinalIgnoreCase);
        int end = path.LastIndexOf('#');
        if (start < 0 || end <= start) return path;
        start += "MMDEVAPI#".Length;
        return path[start..end];
    }
}
