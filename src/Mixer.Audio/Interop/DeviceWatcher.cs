using System.Runtime.InteropServices;

namespace Mixer.Audio.Interop;

public enum DeviceChangeKind
{
    Added,
    Removed,
    /// <summary>Устройство включили, выключили, отключили или вынули (DEVICE_STATE_*).</summary>
    State,
    /// <summary>Сменилось устройство по умолчанию для одной из ролей.</summary>
    Default,
    /// <summary>Сменился формат микса (частота, разрядность, каналы).</summary>
    Format,
    /// <summary>Устройство переименовали.</summary>
    Name,
}

/// <param name="EndpointId">ID эндпоинта; у <see cref="DeviceChangeKind.Default"/> — новое устройство по умолчанию (null — его нет).</param>
public sealed record DeviceChange(DeviceChangeKind Kind, string? EndpointId, EDataFlow Flow = EDataFlow.All, ERole Role = ERole.Console);

/// <summary>IMMNotificationClient. Публичный: CCW отвечает на QueryInterface только для видимых COM-интерфейсов.</summary>
[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string? deviceId, uint newState);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string? deviceId);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string? deviceId);
    [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string? deviceId, PropertyKey key);
}

/// <summary>
/// Горячая замена устройств: подписка IMMNotificationClient. Создавать и освобождать в MTA-потоке, не из обработчика.
/// <see cref="Changed"/> приходит в потоке аудиослужбы: обработчик должен только поставить работу в очередь —
/// ждать, звать WASAPI или отписываться оттуда нельзя (так требует IMMNotificationClient).
/// Из свойств передаются только имя и формат: остальные меняются часто и маршрутизации не касаются.
/// </summary>
public sealed class DeviceWatcher : IDisposable
{
    private IMMDeviceEnumerator? _enumerator;
    private IntPtr _client;
    private readonly Client _sink;

    public event Action<DeviceChange>? Changed;

    private DeviceWatcher()
    {
        _sink = new Client(this);
    }

    /// <summary>Подписаться; null и причина, если Windows подписку не приняла.</summary>
    public static DeviceWatcher? TryCreate(out string? error)
    {
        var w = new DeviceWatcher();
        try
        {
            w._enumerator = DeviceCatalog.CreateEnumerator();
            w._client = Marshal.GetComInterfaceForObject<Client, IMMNotificationClient>(w._sink);
            int hr = w._enumerator.RegisterEndpointNotificationCallback(w._client);
            if (hr < 0)
            {
                error = "RegisterEndpointNotificationCallback: " + Hr.Describe(hr);
                w.Dispose();
                return null;
            }
            error = null;
            return w;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            error = ex.Message;
            w.Dispose();
            return null;
        }
    }

    private void Raise(DeviceChange change)
    {
        try
        {
            Changed?.Invoke(change);
        }
        catch (Exception)
        {
            // исключение не должно уйти в аудиослужбу
        }
    }

    public void Dispose()
    {
        if (_client != IntPtr.Zero)
        {
            _enumerator?.UnregisterEndpointNotificationCallback(_client);
            Marshal.Release(_client);
            _client = IntPtr.Zero;
        }
        if (_enumerator is not null)
        {
            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }
    }

    /// <summary>Отдельный объект: CCW держит ссылку на него, а не на весь наблюдатель.</summary>
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class Client : IMMNotificationClient
    {
        private readonly DeviceWatcher _owner;

        internal Client(DeviceWatcher owner) => _owner = owner;

        public int OnDeviceStateChanged(string? deviceId, uint newState)
        {
            _owner.Raise(new DeviceChange(DeviceChangeKind.State, deviceId));
            return 0;
        }

        public int OnDeviceAdded(string? deviceId)
        {
            _owner.Raise(new DeviceChange(DeviceChangeKind.Added, deviceId));
            return 0;
        }

        public int OnDeviceRemoved(string? deviceId)
        {
            _owner.Raise(new DeviceChange(DeviceChangeKind.Removed, deviceId));
            return 0;
        }

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            _owner.Raise(new DeviceChange(DeviceChangeKind.Default, defaultDeviceId, flow, role));
            return 0;
        }

        public int OnPropertyValueChanged(string? deviceId, PropertyKey key)
        {
            if (Same(key, AudioConst.PKEY_AudioEngine_DeviceFormat))
                _owner.Raise(new DeviceChange(DeviceChangeKind.Format, deviceId));
            else if (Same(key, AudioConst.PKEY_Device_FriendlyName))
                _owner.Raise(new DeviceChange(DeviceChangeKind.Name, deviceId));
            return 0;
        }

        private static bool Same(PropertyKey a, PropertyKey b) => a.FormatId == b.FormatId && a.PropertyId == b.PropertyId;
    }
}
