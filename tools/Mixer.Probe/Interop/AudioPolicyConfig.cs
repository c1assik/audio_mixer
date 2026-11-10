using System.Runtime.InteropServices;

namespace Mixer.Probe.Interop;

/// <summary>
/// Недокументированная фабрика Windows.Media.Internal.AudioPolicyConfig: устройство вывода для отдельного приложения.
/// Раскладка как в EarTrumpet: IInspectable (6 слотов), 19 заглушек, затем
/// SetPersistedDefaultAudioEndpoint (25), GetPersistedDefaultAudioEndpoint (26), ClearAllPersistedApplicationDefaultEndpoints (27).
/// Методы вызываются напрямую через vtable, потому что .NET 8 не маршалит HSTRING и IInspectable.
/// </summary>
public sealed unsafe class AudioPolicyConfig : IDisposable
{
    private const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";
    private const int SlotSet = 25;
    private const int SlotGet = 26;

    private static readonly (string Name, Guid Iid)[] Variants =
    {
        ("21H2+ (ab3d4648)", new Guid("ab3d4648-e242-459f-b02f-541c70306324")),
        ("downlevel (2a59116d)", new Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258")),
    };

    private IntPtr _factory;

    public string Variant { get; }

    private AudioPolicyConfig(IntPtr factory, string variant)
    {
        _factory = factory;
        Variant = variant;
    }

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
            throw new AudioException("IAudioPolicyConfig недоступен: " + string.Join("; ", errors));
        }
        finally
        {
            NativeMethods.WindowsDeleteString(hClass);
        }
    }

    /// <summary>deviceInterfacePath = null сбрасывает привязку на «по умолчанию».</summary>
    public int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, string? deviceInterfacePath)
    {
        IntPtr hs = IntPtr.Zero;
        if (!string.IsNullOrEmpty(deviceInterfacePath))
            AudioException.Check(NativeMethods.WindowsCreateString(deviceInterfacePath, (uint)deviceInterfacePath.Length, out hs), "WindowsCreateString");
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

    public int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out string? deviceInterfacePath)
    {
        IntPtr hs = IntPtr.Zero;
        var fn = (delegate* unmanaged<IntPtr, uint, int, int, IntPtr*, int>)Slot(SlotGet);
        int hr = fn(_factory, processId, (int)flow, (int)role, &hs);
        deviceInterfacePath = null;
        if (hs != IntPtr.Zero)
        {
            IntPtr raw = NativeMethods.WindowsGetStringRawBuffer(hs, out uint len);
            deviceInterfacePath = Marshal.PtrToStringUni(raw, (int)len);
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
