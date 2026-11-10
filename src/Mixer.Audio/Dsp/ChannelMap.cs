using Mixer.Audio.Interop;

namespace Mixer.Audio.Dsp;

/// <summary>
/// Перевод между форматом устройства и внутренним float32-стерео.
/// Вход: моно дублируется в оба канала, многоканальный сигнал сводится по ITU (центр и тыл −3 дБ, LFE отбрасывается).
/// Выход: L/R уходят в FL/FR, остальные каналы — нули; моноустройство получает (L+R)/2.
/// </summary>
public sealed class ChannelMap
{
    private const float M3dB = 0.70710678f;

    private readonly float[] _toL;
    private readonly float[] _toR;
    private readonly int _outL;
    private readonly int _outR;
    private readonly bool _stereoPassthrough;

    public WaveFormat Format { get; }

    public ChannelMap(WaveFormat format)
    {
        Format = format;
        int ch = format.Channels;
        _toL = new float[ch];
        _toR = new float[ch];

        if (ch == 1)
        {
            _toL[0] = _toR[0] = 1f;
            _outL = _outR = 0;
            return;
        }

        // Порядок каналов в потоке совпадает с порядком битов маски по возрастанию.
        int idx = 0;
        _outL = _outR = -1;
        for (int bit = 0; bit < 32 && idx < ch; bit++)
        {
            uint speaker = 1u << bit;
            if ((format.ChannelMask & speaker) == 0) continue;
            (float l, float r) = DownmixCoefficients(speaker);
            _toL[idx] = l;
            _toR[idx] = r;
            if (speaker == Speakers.FrontLeft) _outL = idx;
            if (speaker == Speakers.FrontRight) _outR = idx;
            idx++;
        }
        if (_outL < 0) _outL = 0;
        if (_outR < 0) _outR = Math.Min(1, ch - 1);
        _stereoPassthrough = ch == 2 && _outL == 0 && _outR == 1;
    }

    public static (float L, float R) DownmixCoefficients(uint speaker) => speaker switch
    {
        Speakers.FrontLeft => (1f, 0f),
        Speakers.FrontRight => (0f, 1f),
        Speakers.FrontCenter => (M3dB, M3dB),
        Speakers.LowFrequency => (0f, 0f),
        Speakers.BackLeft => (M3dB, 0f),
        Speakers.BackRight => (0f, M3dB),
        Speakers.FrontLeftOfCenter => (1f, 0f),
        Speakers.FrontRightOfCenter => (0f, 1f),
        Speakers.BackCenter => (0.5f, 0.5f),
        Speakers.SideLeft => (M3dB, 0f),
        Speakers.SideRight => (0f, M3dB),
        Speakers.TopCenter => (0.5f, 0.5f),
        Speakers.TopFrontLeft => (M3dB, 0f),
        Speakers.TopFrontCenter => (0.5f, 0.5f),
        Speakers.TopFrontRight => (0f, M3dB),
        Speakers.TopBackLeft => (M3dB, 0f),
        Speakers.TopBackCenter => (0.5f, 0.5f),
        Speakers.TopBackRight => (0f, M3dB),
        _ => (0f, 0f),
    };

    /// <summary>Кадры устройства → interleaved-стерео float.</summary>
    public unsafe void Decode(IntPtr data, int frames, Span<float> stereo)
    {
        int ch = Format.Channels;
        switch (Format.Type)
        {
            case SampleType.Float32:
            {
                var src = new ReadOnlySpan<float>((void*)data, frames * ch);
                if (_stereoPassthrough)
                {
                    src.CopyTo(stereo);
                    return;
                }
                for (int f = 0, o = 0, s = 0; f < frames; f++, o += 2, s += ch)
                {
                    float l = 0, r = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        float v = src[s + c];
                        l += v * _toL[c];
                        r += v * _toR[c];
                    }
                    stereo[o] = l;
                    stereo[o + 1] = r;
                }
                return;
            }
            case SampleType.Int16:
            {
                var src = new ReadOnlySpan<short>((void*)data, frames * ch);
                const float k = 1f / 32768f;
                for (int f = 0, o = 0, s = 0; f < frames; f++, o += 2, s += ch)
                {
                    float l = 0, r = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        float v = src[s + c] * k;
                        l += v * _toL[c];
                        r += v * _toR[c];
                    }
                    stereo[o] = l;
                    stereo[o + 1] = r;
                }
                return;
            }
            case SampleType.Int24:
            {
                byte* p = (byte*)data;
                const float k = 1f / 2147483648f;
                for (int f = 0, o = 0; f < frames; f++, o += 2)
                {
                    float l = 0, r = 0;
                    for (int c = 0; c < ch; c++, p += 3)
                    {
                        int iv = (p[0] << 8) | (p[1] << 16) | (p[2] << 24);
                        float v = iv * k;
                        l += v * _toL[c];
                        r += v * _toR[c];
                    }
                    stereo[o] = l;
                    stereo[o + 1] = r;
                }
                return;
            }
            case SampleType.Int32:
            {
                var src = new ReadOnlySpan<int>((void*)data, frames * ch);
                const float k = 1f / 2147483648f;
                for (int f = 0, o = 0, s = 0; f < frames; f++, o += 2, s += ch)
                {
                    float l = 0, r = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        float v = src[s + c] * k;
                        l += v * _toL[c];
                        r += v * _toR[c];
                    }
                    stereo[o] = l;
                    stereo[o + 1] = r;
                }
                return;
            }
            default:
                stereo[..(frames * 2)].Clear();
                return;
        }
    }

    /// <summary>Interleaved-стерео float → кадры устройства. Сигнал уже ограничен ±1.</summary>
    public unsafe void Encode(ReadOnlySpan<float> stereo, int frames, IntPtr data)
    {
        int ch = Format.Channels;
        switch (Format.Type)
        {
            case SampleType.Float32:
            {
                var dst = new Span<float>((void*)data, frames * ch);
                if (_stereoPassthrough)
                {
                    stereo[..(frames * 2)].CopyTo(dst);
                    return;
                }
                if (ch > 2) dst.Clear();
                for (int f = 0, i = 0, d = 0; f < frames; f++, i += 2, d += ch)
                {
                    if (ch == 1)
                    {
                        dst[d] = 0.5f * (stereo[i] + stereo[i + 1]);
                        continue;
                    }
                    dst[d + _outL] = stereo[i];
                    dst[d + _outR] = stereo[i + 1];
                }
                return;
            }
            case SampleType.Int16:
            {
                var dst = new Span<short>((void*)data, frames * ch);
                if (ch > 2) dst.Clear();
                for (int f = 0, i = 0, d = 0; f < frames; f++, i += 2, d += ch)
                {
                    if (ch == 1)
                    {
                        dst[d] = ToInt16(0.5f * (stereo[i] + stereo[i + 1]));
                        continue;
                    }
                    dst[d + _outL] = ToInt16(stereo[i]);
                    dst[d + _outR] = ToInt16(stereo[i + 1]);
                }
                return;
            }
            case SampleType.Int24:
            {
                var dst = new Span<byte>((void*)data, frames * ch * 3);
                dst.Clear();
                for (int f = 0, i = 0; f < frames; f++, i += 2)
                {
                    int baseOff = f * ch * 3;
                    if (ch == 1)
                    {
                        Put24(dst, baseOff, 0.5f * (stereo[i] + stereo[i + 1]));
                        continue;
                    }
                    Put24(dst, baseOff + _outL * 3, stereo[i]);
                    Put24(dst, baseOff + _outR * 3, stereo[i + 1]);
                }
                return;
            }
            case SampleType.Int32:
            {
                var dst = new Span<int>((void*)data, frames * ch);
                if (ch > 2) dst.Clear();
                for (int f = 0, i = 0, d = 0; f < frames; f++, i += 2, d += ch)
                {
                    if (ch == 1)
                    {
                        dst[d] = ToInt32(0.5f * (stereo[i] + stereo[i + 1]));
                        continue;
                    }
                    dst[d + _outL] = ToInt32(stereo[i]);
                    dst[d + _outR] = ToInt32(stereo[i + 1]);
                }
                return;
            }
            default:
                new Span<byte>((void*)data, frames * Format.BlockAlign).Clear();
                return;
        }
    }

    private static short ToInt16(float v) => (short)Math.Clamp((int)MathF.Round(v * 32767f), short.MinValue, short.MaxValue);

    private static int ToInt32(float v) => (int)Math.Clamp(Math.Round(v * 2147483647.0), int.MinValue, int.MaxValue);

    private static void Put24(Span<byte> dst, int off, float v)
    {
        int iv = Math.Clamp((int)MathF.Round(v * 8388607f), -8388608, 8388607);
        dst[off] = (byte)iv;
        dst[off + 1] = (byte)(iv >> 8);
        dst[off + 2] = (byte)(iv >> 16);
    }
}
