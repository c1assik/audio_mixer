using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Mixer.Probe.Interop;

namespace Mixer.Probe.Audio;

/// <summary>WAVEFORMATEX / WAVEFORMATEXTENSIBLE в виде байтов плюс разобранные поля.</summary>
public sealed class AudioFormat
{
    private readonly byte[] _raw;

    public ushort Tag { get; }
    public int Channels { get; }
    public int SampleRate { get; }
    public int BlockAlign { get; }
    public int BitsPerSample { get; }
    public bool IsFloat { get; }

    private AudioFormat(byte[] raw)
    {
        _raw = raw;
        Tag = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0));
        Channels = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(2));
        SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4));
        BlockAlign = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(12));
        BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(14));
        IsFloat = Tag == AudioConst.WAVE_FORMAT_IEEE_FLOAT
                  || (Tag == AudioConst.WAVE_FORMAT_EXTENSIBLE && raw.Length >= 40
                      && new Guid(raw.AsSpan(24, 16)) == AudioConst.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
    }

    public static AudioFormat Simple(int sampleRate, int channels, bool isFloat)
    {
        int bits = isFloat ? 32 : 16;
        int block = channels * bits / 8;
        bool extensible = channels > 2; // для >2 каналов нужен WAVEFORMATEXTENSIBLE с маской каналов
        var raw = new byte[extensible ? 40 : 18];
        var s = raw.AsSpan();
        ushort tag = extensible ? AudioConst.WAVE_FORMAT_EXTENSIBLE : isFloat ? AudioConst.WAVE_FORMAT_IEEE_FLOAT : AudioConst.WAVE_FORMAT_PCM;
        BinaryPrimitives.WriteUInt16LittleEndian(s[0..], tag);
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)(sampleRate * block));
        BinaryPrimitives.WriteUInt16LittleEndian(s[12..], (ushort)block);
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], (ushort)bits);
        BinaryPrimitives.WriteUInt16LittleEndian(s[16..], (ushort)(extensible ? 22 : 0));
        if (extensible)
        {
            uint mask = channels switch
            {
                6 => 0x3F,   // 5.1: FL FR FC LFE BL BR
                8 => 0x63F,  // 7.1: FL FR FC LFE BL BR SL SR
                _ => (1u << channels) - 1,
            };
            BinaryPrimitives.WriteUInt16LittleEndian(s[18..], (ushort)bits);
            BinaryPrimitives.WriteUInt32LittleEndian(s[20..], mask);
            var sub = isFloat ? AudioConst.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT : new Guid("00000001-0000-0010-8000-00aa00389b71");
            sub.TryWriteBytes(s[24..]);
        }
        return new AudioFormat(raw);
    }

    /// <summary>Из содержимого чанка fmt (16, 18 или 40 байт).</summary>
    public static AudioFormat FromFmtChunk(ReadOnlySpan<byte> fmt)
    {
        var raw = new byte[Math.Max(18, fmt.Length)];
        fmt.CopyTo(raw);
        return new AudioFormat(raw);
    }

    public static AudioFormat FromPointer(IntPtr p)
    {
        int cbSize = (ushort)Marshal.ReadInt16(p, 16);
        var raw = new byte[18 + cbSize];
        Marshal.Copy(p, raw, 0, raw.Length);
        return new AudioFormat(raw);
    }

    /// <summary>Копия структуры в CoTaskMem; освобождать через Marshal.FreeCoTaskMem.</summary>
    public IntPtr AllocCoTaskMem()
    {
        IntPtr p = Marshal.AllocCoTaskMem(_raw.Length);
        Marshal.Copy(_raw, 0, p, _raw.Length);
        return p;
    }

    /// <summary>Содержимое чанка fmt для WAV.</summary>
    public ReadOnlySpan<byte> FmtChunk =>
        Tag == AudioConst.WAVE_FORMAT_PCM && _raw.Length == 18 ? _raw.AsSpan(0, 16) : _raw;

    /// <summary>Перевод interleaved-сэмплов в float [-1; 1].</summary>
    public void Decode(ReadOnlySpan<byte> src, Span<float> dst)
    {
        int bytesPerSample = BlockAlign / Channels;
        int n = Math.Min(dst.Length, src.Length / bytesPerSample);
        if (IsFloat && bytesPerSample == 4)
        {
            MemoryMarshal.Cast<byte, float>(src[..(n * 4)]).CopyTo(dst);
        }
        else if (bytesPerSample == 2)
        {
            var s16 = MemoryMarshal.Cast<byte, short>(src[..(n * 2)]);
            for (int i = 0; i < n; i++) dst[i] = s16[i] / 32768f;
        }
        else if (bytesPerSample == 3)
        {
            for (int i = 0; i < n; i++)
            {
                int o = i * 3;
                int v = (src[o] << 8) | (src[o + 1] << 16) | (src[o + 2] << 24);
                dst[i] = v / 2147483648f;
            }
        }
        else if (bytesPerSample == 4)
        {
            var s32 = MemoryMarshal.Cast<byte, int>(src[..(n * 4)]);
            for (int i = 0; i < n; i++) dst[i] = s32[i] / 2147483648f;
        }
        else
        {
            dst[..n].Clear();
        }
    }

    public override string ToString()
    {
        string kind = IsFloat ? "float32" : $"PCM{BitsPerSample}";
        return $"{kind} {SampleRate} Гц {Channels} кан.";
    }
}
