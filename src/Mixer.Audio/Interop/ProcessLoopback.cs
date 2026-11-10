using System.Runtime.InteropServices;
using Mixer.Audio.Resources;

namespace Mixer.Audio.Interop;

/// <summary>
/// Активация IAudioClient для process loopback через ActivateAudioInterfaceAsync (перенесено из Mixer.Probe, этап 0).
/// Официальный минимум — Windows 10 build 20348, но API работает и на части более старых сборок,
/// поэтому доступность проверяется пробной активацией (<see cref="Check"/>), а не номером сборки.
/// </summary>
public static class ProcessLoopback
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Клиент захвата звука дерева процессов. exclude = false — только дерево processId (узел «Приложение»),
    /// exclude = true — весь звук системы, кроме дерева processId (узел «Весь звук системы», processId — сам микшер).
    /// </summary>
    internal static IAudioClient Activate(uint processId, bool exclude, TimeSpan? timeout = null)
    {
        var activation = new AudioClientActivationParams
        {
            ActivationType = AudioConst.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            TargetProcessId = processId,
            ProcessLoopbackMode = exclude
                ? AudioConst.PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
                : AudioConst.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE,
        };

        int paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr pParams = Marshal.AllocHGlobal(paramsSize);
        IntPtr pVariant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
        try
        {
            Marshal.StructureToPtr(activation, pParams, false);
            var variant = new PropVariant { vt = AudioConst.VT_BLOB, blobSize = (uint)paramsSize, blobData = pParams };
            Marshal.StructureToPtr(variant, pVariant, false);

            var handler = new ActivationHandler();
            Guid iid = typeof(IAudioClient).GUID;
            int hr = NativeMethods.ActivateAudioInterfaceAsync(NativeMethods.VirtualAudioDeviceProcessLoopback, ref iid, pVariant, handler, out var operation);
            AudioException.Check(hr, "ActivateAudioInterfaceAsync");
            // Параметры копируются при вызове, но ждём завершения до освобождения памяти — на случай ленивого чтения.
            if (!handler.Done.Wait(timeout ?? DefaultTimeout))
                throw new AudioException(AudioStrings.Loopback_Timeout);
            GC.KeepAlive(operation);
            if (handler.Error is { } err) throw err;
            return handler.Client!;
        }
        finally
        {
            Marshal.FreeHGlobal(pVariant);
            Marshal.FreeHGlobal(pParams);
        }
    }

    /// <summary>
    /// Пробная активация (exclude по своему PID, без Initialize). null — process loopback доступен, иначе причина.
    /// Вызывать не из потока интерфейса.
    /// </summary>
    public static string? Check(TimeSpan? timeout = null)
    {
        try
        {
            var client = Activate((uint)Environment.ProcessId, exclude: true, timeout ?? TimeSpan.FromSeconds(3));
            Marshal.ReleaseComObject(client);
            return null;
        }
        catch (Exception ex) when (ex is AudioException or COMException or EntryPointNotFoundException or DllNotFoundException or InvalidCastException)
        {
            return Loc.F(AudioStrings.Loopback_UnavailableFormat, ex.Message);
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        internal ManualResetEventSlim Done { get; } = new(false);
        internal IAudioClient? Client { get; private set; }
        internal Exception? Error { get; private set; }

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out int hr, out object iface);
                if (hr < 0 || iface is null)
                    Error = new AudioException(AudioStrings.Loopback_Result, hr < 0 ? hr : unchecked((int)0x80004005));
                else
                    Client = (IAudioClient)iface;
            }
            catch (Exception ex)
            {
                Error = ex;
            }
            finally
            {
                Done.Set();
            }
        }
    }
}
