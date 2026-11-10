using System.Runtime.InteropServices;

namespace Mixer.Probe.Interop;

/// <summary>Активация IAudioClient для process loopback через ActivateAudioInterfaceAsync.</summary>
public static class ProcessLoopback
{
    public static IAudioClient Activate(uint processId, bool excludeTree, TimeSpan? timeout = null)
    {
        var activation = new AudioClientActivationParams
        {
            ActivationType = AudioConst.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            TargetProcessId = processId,
            ProcessLoopbackMode = excludeTree
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
            return ActivateCore(NativeMethods.VirtualAudioDeviceProcessLoopback, pVariant, timeout);
        }
        finally
        {
            Marshal.FreeHGlobal(pVariant);
            Marshal.FreeHGlobal(pParams);
        }
    }

    /// <summary>
    /// Клиент воспроизведения на виртуальном «устройстве по умолчанию» (DEVINTERFACE_AUDIO_RENDER):
    /// automatic stream routing, поток сам переезжает при смене устройства по умолчанию.
    /// </summary>
    public static IAudioClient ActivateDefaultRender(TimeSpan? timeout = null) =>
        ActivateCore(NativeMethods.DefaultAudioRenderInterface, IntPtr.Zero, timeout);

    private static IAudioClient ActivateCore(string path, IntPtr pVariant, TimeSpan? timeout)
    {
        var handler = new ActivationHandler();
        Guid iid = typeof(IAudioClient).GUID;
        int hr = NativeMethods.ActivateAudioInterfaceAsync(path, ref iid, pVariant, handler, out var operation);
        AudioException.Check(hr, "ActivateAudioInterfaceAsync");

        if (!((IAsyncResult)handler.Task).AsyncWaitHandle.WaitOne(timeout ?? TimeSpan.FromSeconds(5)))
            throw new AudioException("ActivateAudioInterfaceAsync не завершилась за отведённое время");

        GC.KeepAlive(operation);
        GC.KeepAlive(handler);
        return handler.Task.GetAwaiter().GetResult();
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAudioClient> Task => _tcs.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out int hr, out object iface);
                if (hr < 0 || iface is null)
                    _tcs.TrySetException(new AudioException("ActivateAudioInterfaceAsync, результат", hr < 0 ? hr : unchecked((int)0x80004005)));
                else
                    _tcs.TrySetResult((IAudioClient)iface);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }
    }
}
