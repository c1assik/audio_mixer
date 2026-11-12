using System.Runtime.InteropServices;

namespace Mixer.Plugins.Host;

/// <summary>
/// Зеркало src/Mixer.PluginHost/src/shared_audio.h. Смещения сверены там через static_assert;
/// при любом изменении — повысить <see cref="ProtocolVersion"/> на обеих сторонах.
/// </summary>
internal static class SharedLayout
{
    public const uint Magic = 0x4850584D; // 'MXPH'
    public const uint ProtocolVersion = 2;
    public const int Channels = 2;
    public const int HeaderBytes = 4096;
    public const int ErrorOffset = 256;
    public const int ErrorBytes = 1024;

    public const uint FlagStop = 1;
    public const uint DoneBypassed = 1;

    public static long SectionBytes(int maxFrames) => HeaderBytes + 2L * Channels * maxFrames * sizeof(float);
    public static long InputOffset(int maxFrames, int ch) => HeaderBytes + (long)ch * maxFrames * sizeof(float);
    public static long OutputOffset(int maxFrames, int ch) => HeaderBytes + (long)(Channels + ch) * maxFrames * sizeof(float);
}

internal enum HostState
{
    Starting = 0,
    Ready = 1,
    Failed = 2,
    Stopped = 3,
}

[StructLayout(LayoutKind.Explicit, Size = SharedLayout.ErrorOffset)]
internal struct SharedHeader
{
    // строка 0: микшер, один раз
    [FieldOffset(0)] public uint Magic;
    [FieldOffset(4)] public uint Version;
    [FieldOffset(8)] public uint SampleRate;
    [FieldOffset(12)] public uint MaxFrames;
    [FieldOffset(16)] public uint Channels;

    // строка 1: хост, при загрузке и изменениях
    [FieldOffset(64)] public int State;
    [FieldOffset(68)] public int LatencySamples;
    [FieldOffset(72)] public int PluginInChannels;
    [FieldOffset(76)] public int PluginOutChannels;
    [FieldOffset(80)] public uint HostPid;

    // строка 2: микшер, на каждом блоке
    [FieldOffset(128)] public ulong RequestSeq;
    [FieldOffset(136)] public long RequestQpc;
    [FieldOffset(144)] public uint Frames;
    [FieldOffset(148)] public uint Flags;

    // строка 3: хост, на каждом блоке
    [FieldOffset(192)] public ulong DoneSeq;
    [FieldOffset(200)] public long WakeQpc;
    [FieldOffset(208)] public long DoneQpc;
    [FieldOffset(216)] public long ProcessTicks;
    [FieldOffset(224)] public int ProcessResult;
    [FieldOffset(228)] public uint DoneFlags;
}
