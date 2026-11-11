using Mixer.Audio.Interop;

namespace Mixer.Core.Apps;

/// <summary>Устройство вывода отдельного приложения (IAudioPolicyConfig). Абстракция — ради тестов без Windows Audio.</summary>
public interface IAppEndpointPolicy
{
    /// <summary>endpointId = null — сбросить на «по умолчанию».</summary>
    int Set(uint pid, ERole role, string? endpointId);

    /// <summary>endpointId = null — привязки нет.</summary>
    int Get(uint pid, ERole role, out string? endpointId);
}

/// <summary>Журнал приложений с привязкой к заглушке (boundApps). Add пишет на диск до возврата; ошибка — исключение.</summary>
public interface IBindingJournal
{
    IReadOnlyCollection<string> Apps { get; }
    void Add(string exe);
    void Remove(string exe);
}

/// <summary>Настоящий IAudioPolicyConfig. Создавать и вызывать в одном MTA-потоке (управляющем), не в потоке интерфейса.</summary>
public sealed class WindowsEndpointPolicy : IAppEndpointPolicy, IDisposable
{
    private readonly AudioPolicyConfig _config;

    private WindowsEndpointPolicy(AudioPolicyConfig config) => _config = config;

    public string Variant => _config.Variant;

    /// <summary>null и причина, если API недоступен в этой сборке Windows.</summary>
    public static WindowsEndpointPolicy? TryCreate(out string? error)
    {
        try
        {
            error = null;
            return new WindowsEndpointPolicy(AudioPolicyConfig.Create());
        }
        catch (Exception ex) when (ex is AudioException or System.Runtime.InteropServices.COMException or EntryPointNotFoundException)
        {
            error = ex.Message;
            return null;
        }
    }

    public int Set(uint pid, ERole role, string? endpointId) =>
        _config.SetPersistedDefaultAudioEndpoint(pid, EDataFlow.Render, role, endpointId);

    public int Get(uint pid, ERole role, out string? endpointId) =>
        _config.GetPersistedDefaultAudioEndpoint(pid, EDataFlow.Render, role, out endpointId);

    /// <summary>
    /// То же для устройства записи: «Использовать как микрофон в…» назначает приложению выход виртуального кабеля
    /// (v2, «Виртуальные кабели»; так делает Earmark). Тот же объект, те же правила PID и E_INVALIDARG.
    /// </summary>
    public IAppEndpointPolicy ForCapture() => new CapturePolicy(_config);

    public void Dispose() => _config.Dispose();

    private sealed class CapturePolicy(AudioPolicyConfig config) : IAppEndpointPolicy
    {
        public int Set(uint pid, ERole role, string? endpointId) =>
            config.SetPersistedDefaultAudioEndpoint(pid, EDataFlow.Capture, role, endpointId);

        public int Get(uint pid, ERole role, out string? endpointId) =>
            config.GetPersistedDefaultAudioEndpoint(pid, EDataFlow.Capture, role, out endpointId);
    }
}
