using System.Runtime.InteropServices;

namespace Mixer.Core.Sys;

/// <summary>
/// Сон и пробуждение: PowerRegisterSuspendResumeNotification с обратным вызовом. Это те же PBT_APMSUSPEND и
/// PBT_APMRESUME*, что приходят окну в WM_POWERBROADCAST, но без окна — работает и в консольном хосте.
/// <see cref="Changed"/> приходит в системном потоке: обработчик должен только поставить работу в очередь.
/// </summary>
public sealed class PowerEvents : IDisposable
{
    private const uint DEVICE_NOTIFY_CALLBACK = 2;
    private const uint PBT_APMSUSPEND = 0x4;
    private const uint PBT_APMRESUMESUSPEND = 0x7;
    private const uint PBT_APMRESUMEAUTOMATIC = 0x12;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint Callback(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct SubscribeParameters
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerRegisterSuspendResumeNotification(uint flags, IntPtr recipient, out IntPtr registrationHandle);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);

    // делегат и структура живут, пока жива подписка: система зовёт их по указателю
    private readonly Callback _callback;
    private IntPtr _params;
    private IntPtr _handle;

    /// <summary>true — система засыпает, false — проснулась.</summary>
    public event Action<bool>? Changed;

    private PowerEvents()
    {
        _callback = OnPower;
    }

    public static PowerEvents? TryCreate(out string? error)
    {
        var p = new PowerEvents();
        try
        {
            p._params = Marshal.AllocHGlobal(Marshal.SizeOf<SubscribeParameters>());
            Marshal.StructureToPtr(new SubscribeParameters { Callback = Marshal.GetFunctionPointerForDelegate(p._callback) }, p._params, false);
            uint rc = PowerRegisterSuspendResumeNotification(DEVICE_NOTIFY_CALLBACK, p._params, out p._handle);
            if (rc != 0)
            {
                error = $"PowerRegisterSuspendResumeNotification: code {rc}";
                p.Dispose();
                return null;
            }
            error = null;
            return p;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            error = ex.Message;
            p.Dispose();
            return null;
        }
    }

    private uint OnPower(IntPtr context, uint type, IntPtr setting)
    {
        try
        {
            switch (type)
            {
                case PBT_APMSUSPEND: Changed?.Invoke(true); break;
                case PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND: Changed?.Invoke(false); break;
            }
        }
        catch (Exception)
        {
            // исключение не должно уйти в систему
        }
        return 0;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            PowerUnregisterSuspendResumeNotification(_handle);
            _handle = IntPtr.Zero;
        }
        if (_params != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_params);
            _params = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Запасной детектор сна, если уведомление не пришло: GetTickCount64 идёт и во сне, QueryUnbiasedInterruptTime — нет.
/// Разница между ними растёт только пока система спит.
/// </summary>
public sealed class SleepDetector
{
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    private readonly Func<double> _withSleep;
    private readonly Func<double> _withoutSleep;
    private double _bias;

    /// <param name="withSleep">Секунды с учётом сна (тесты); по умолчанию GetTickCount64.</param>
    /// <param name="withoutSleep">Секунды без сна (тесты); по умолчанию QueryUnbiasedInterruptTime.</param>
    public SleepDetector(Func<double>? withSleep = null, Func<double>? withoutSleep = null)
    {
        _withSleep = withSleep ?? (() => Environment.TickCount64 / 1000.0);
        _withoutSleep = withoutSleep ?? (() => QueryUnbiasedInterruptTime(out ulong t) ? t / 1e7 : Environment.TickCount64 / 1000.0);
        _bias = Bias();
    }

    private double Bias() => _withSleep() - _withoutSleep();

    /// <summary>Сколько секунд система проспала с прошлого вызова (дрожание часов меньше 1 с не считается).</summary>
    public double SleptSeconds()
    {
        double bias = Bias();
        double slept = bias - _bias;
        _bias = bias;
        return slept >= 1.0 ? slept : 0;
    }
}
