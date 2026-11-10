using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Mixer.Audio.Resources;

namespace Mixer.Audio.Interop;

public enum SampleType { Float32, Int16, Int24, Int32, Unsupported }

/// <summary>Разобранный WAVEFORMATEX / WAVEFORMATEXTENSIBLE.</summary>
public sealed class WaveFormat
{
    public int SampleRate { get; }
    public int Channels { get; }
    public int BlockAlign { get; }
    public int BitsPerSample { get; }
    public int ValidBits { get; }
    /// <summary>Маска каналов (SPEAKER_*). Для WAVEFORMATEX без маски подставляется стандартная.</summary>
    public uint ChannelMask { get; }
    public SampleType Type { get; }

    public WaveFormat(int sampleRate, int channels, SampleType type, uint channelMask = 0)
    {
        SampleRate = sampleRate;
        Channels = channels;
        Type = type;
        BitsPerSample = type switch { SampleType.Int16 => 16, SampleType.Int24 => 24, _ => 32 };
        ValidBits = BitsPerSample;
        BlockAlign = channels * BitsPerSample / 8;
        ChannelMask = channelMask != 0 ? channelMask : DefaultMask(channels);
    }

    private WaveFormat(ReadOnlySpan<byte> raw)
    {
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(raw);
        Channels = BinaryPrimitives.ReadUInt16LittleEndian(raw[2..]);
        SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]);
        BlockAlign = BinaryPrimitives.ReadUInt16LittleEndian(raw[12..]);
        BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(raw[14..]);
        ValidBits = BitsPerSample;
        bool isFloat = tag == AudioConst.WAVE_FORMAT_IEEE_FLOAT;
        bool isPcm = tag == AudioConst.WAVE_FORMAT_PCM;
        uint mask = 0;
        if (tag == AudioConst.WAVE_FORMAT_EXTENSIBLE && raw.Length >= 40)
        {
            ValidBits = BinaryPrimitives.ReadUInt16LittleEndian(raw[18..]);
            if (ValidBits == 0) ValidBits = BitsPerSample;
            mask = BinaryPrimitives.ReadUInt32LittleEndian(raw[20..]);
            var sub = new Guid(raw.Slice(24, 16));
            isFloat = sub == AudioConst.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
            isPcm = sub == AudioConst.KSDATAFORMAT_SUBTYPE_PCM;
        }
        ChannelMask = mask != 0 && BitCount(mask) == Channels ? mask : DefaultMask(Channels);
        int bytesPerSample = Channels > 0 ? BlockAlign / Channels : 0;
        Type = (isFloat, isPcm, bytesPerSample) switch
        {
            (true, _, 4) => SampleType.Float32,
            (_, true, 2) => SampleType.Int16,
            (_, true, 3) => SampleType.Int24,
            (_, true, 4) => SampleType.Int32,
            _ => SampleType.Unsupported,
        };
    }

    /// <summary>
    /// Формат захвата process loopback: float32 7.1 (маска 0x63F). Стерео-контент лежит во FL/FR с исходным уровнем;
    /// стерео-запрос дал бы нормированный даунмикс Windows, на 13,5 дБ тише на 7.1-устройстве (этап 0).
    /// </summary>
    public static WaveFormat ProcessLoopback(int sampleRate) => new(sampleRate, 8, SampleType.Float32, 0x63F);

    /// <summary>WAVEFORMATEXTENSIBLE (40 байт) для float и для больше чем двух каналов, иначе WAVEFORMATEX (18 байт).</summary>
    public byte[] ToBytes()
    {
        bool isFloat = Type == SampleType.Float32;
        bool extensible = isFloat || Channels > 2 || ValidBits != BitsPerSample;
        var raw = new byte[extensible ? 40 : 18];
        var s = raw.AsSpan();
        ushort tag = extensible ? AudioConst.WAVE_FORMAT_EXTENSIBLE : AudioConst.WAVE_FORMAT_PCM;
        BinaryPrimitives.WriteUInt16LittleEndian(s, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], (ushort)Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)(SampleRate * BlockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(s[12..], (ushort)BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], (ushort)BitsPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(s[16..], (ushort)(extensible ? 22 : 0));
        if (extensible)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s[18..], (ushort)ValidBits);
            BinaryPrimitives.WriteUInt32LittleEndian(s[20..], ChannelMask);
            (isFloat ? AudioConst.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT : AudioConst.KSDATAFORMAT_SUBTYPE_PCM).TryWriteBytes(s[24..]);
        }
        return raw;
    }

    /// <summary>Разбор сырых байт WAVEFORMATEX / WAVEFORMATEXTENSIBLE.</summary>
    public static WaveFormat FromBytes(ReadOnlySpan<byte> raw) => new(raw);

    /// <summary>Копия структуры в CoTaskMem; освобождать через Marshal.FreeCoTaskMem.</summary>
    internal IntPtr AllocCoTaskMem()
    {
        var raw = ToBytes();
        IntPtr p = Marshal.AllocCoTaskMem(raw.Length);
        Marshal.Copy(raw, 0, p, raw.Length);
        return p;
    }

    internal static WaveFormat FromPointer(IntPtr p)
    {
        int cbSize = (ushort)Marshal.ReadInt16(p, 16);
        ushort tag = (ushort)Marshal.ReadInt16(p, 0);
        int len = tag == AudioConst.WAVE_FORMAT_PCM ? 16 : 18 + cbSize;
        var raw = new byte[Math.Max(len, 18)];
        Marshal.Copy(p, raw, 0, len);
        return new WaveFormat(raw);
    }

    public static uint DefaultMask(int channels) => channels switch
    {
        1 => Speakers.FrontCenter,
        2 => Speakers.FrontLeft | Speakers.FrontRight,
        3 => 0x7,
        4 => 0x33,  // квадро: FL FR BL BR
        6 => 0x3F,  // 5.1: FL FR FC LFE BL BR
        8 => 0x63F, // 7.1: FL FR FC LFE BL BR SL SR
        _ => channels >= 32 ? 0xFFFFFFFF : (1u << channels) - 1,
    };

    private static int BitCount(uint v) => System.Numerics.BitOperations.PopCount(v);

    public override string ToString()
    {
        string kind = Type switch
        {
            SampleType.Float32 => "float32",
            SampleType.Int16 => "pcm16",
            SampleType.Int24 => "pcm24",
            SampleType.Int32 => ValidBits == 24 ? "pcm24in32" : "pcm32",
            _ => "?",
        };
        return Loc.F(AudioStrings.Format_DescriptionFormat, kind, SampleRate, Channels);
    }
}

public static class Speakers
{
    public const uint FrontLeft = 0x1;
    public const uint FrontRight = 0x2;
    public const uint FrontCenter = 0x4;
    public const uint LowFrequency = 0x8;
    public const uint BackLeft = 0x10;
    public const uint BackRight = 0x20;
    public const uint FrontLeftOfCenter = 0x40;
    public const uint FrontRightOfCenter = 0x80;
    public const uint BackCenter = 0x100;
    public const uint SideLeft = 0x200;
    public const uint SideRight = 0x400;
    public const uint TopCenter = 0x800;
    public const uint TopFrontLeft = 0x1000;
    public const uint TopFrontCenter = 0x2000;
    public const uint TopFrontRight = 0x4000;
    public const uint TopBackLeft = 0x8000;
    public const uint TopBackCenter = 0x10000;
    public const uint TopBackRight = 0x20000;
}
