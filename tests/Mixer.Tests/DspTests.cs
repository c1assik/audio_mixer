using System.Runtime.InteropServices;
using Mixer.Audio.Dsp;
using Mixer.Audio.Interop;

namespace Mixer.Tests;

public class ChannelMapTests
{
    private const float M3 = 0.70710678f;

    private static unsafe float[] Decode(WaveFormat fmt, float[] frame)
    {
        var map = new ChannelMap(fmt);
        var stereo = new float[2];
        fixed (float* p = frame) map.Decode((IntPtr)p, 1, stereo);
        return stereo;
    }

    [Fact]
    public void Mono_is_duplicated_to_both_channels()
    {
        var s = Decode(new WaveFormat(48000, 1, SampleType.Float32), [0.5f]);
        Assert.Equal([0.5f, 0.5f], s);
    }

    [Fact]
    public void Stereo_passes_through()
    {
        var s = Decode(new WaveFormat(48000, 2, SampleType.Float32), [0.25f, -0.5f]);
        Assert.Equal([0.25f, -0.5f], s);
    }

    [Theory]
    // 7.1, маска 0x63F: FL FR FC LFE BL BR SL SR
    [InlineData(0, 1f, 0f)]
    [InlineData(1, 0f, 1f)]
    [InlineData(2, M3, M3)]
    [InlineData(3, 0f, 0f)]
    [InlineData(4, M3, 0f)]
    [InlineData(5, 0f, M3)]
    [InlineData(6, M3, 0f)]
    [InlineData(7, 0f, M3)]
    public void Itu_downmix_7_1(int channel, float l, float r)
    {
        var frame = new float[8];
        frame[channel] = 1f;
        var s = Decode(new WaveFormat(48000, 8, SampleType.Float32, 0x63F), frame);
        Assert.Equal(l, s[0], 5);
        Assert.Equal(r, s[1], 5);
    }

    [Fact]
    public void Itu_downmix_5_1_side()
    {
        // 5.1 (side), маска 0x60F: FL FR FC LFE SL SR
        var s = Decode(new WaveFormat(48000, 6, SampleType.Float32, 0x60F), [0, 0, 0, 0, 1, 0]);
        Assert.Equal(M3, s[0], 5);
        Assert.Equal(0f, s[1], 5);
    }

    [Fact]
    public void Stereo_content_in_7_1_capture_keeps_level()
    {
        // этап 0: стерео-контент лежит во FL/FR, тыл пустой — уровень не должен меняться
        var s = Decode(new WaveFormat(48000, 8, SampleType.Float32, 0x63F), [0.3f, -0.3f, 0, 0, 0, 0, 0, 0]);
        Assert.Equal([0.3f, -0.3f], s);
    }

    [Fact]
    public unsafe void Int16_and_int24_decode()
    {
        var map16 = new ChannelMap(new WaveFormat(48000, 2, SampleType.Int16));
        short[] pcm = [16384, -32768];
        var st = new float[2];
        fixed (short* p = pcm) map16.Decode((IntPtr)p, 1, st);
        Assert.Equal(0.5f, st[0], 4);
        Assert.Equal(-1f, st[1], 4);

        var map24 = new ChannelMap(new WaveFormat(48000, 2, SampleType.Int24));
        byte[] b24 = [0x00, 0x00, 0x40, 0x00, 0x00, 0xC0]; // +0,5 и −0,5
        fixed (byte* p = b24) map24.Decode((IntPtr)p, 1, st);
        Assert.Equal(0.5f, st[0], 4);
        Assert.Equal(-0.5f, st[1], 4);
    }

    [Fact]
    public unsafe void Encode_to_7_1_writes_front_only()
    {
        var map = new ChannelMap(new WaveFormat(48000, 8, SampleType.Float32, 0x63F));
        var dst = new float[16];
        Array.Fill(dst, 9f);
        fixed (float* p = dst) map.Encode([0.1f, 0.2f, 0.3f, 0.4f], 2, (IntPtr)p);
        Assert.Equal([0.1f, 0.2f, 0, 0, 0, 0, 0, 0, 0.3f, 0.4f, 0, 0, 0, 0, 0, 0], dst);
    }

    [Fact]
    public unsafe void Encode_to_mono_and_int16()
    {
        var mono = new ChannelMap(new WaveFormat(48000, 1, SampleType.Float32));
        var m = new float[1];
        fixed (float* p = m) mono.Encode([0.2f, 0.4f], 1, (IntPtr)p);
        Assert.Equal(0.3f, m[0], 5);

        var i16 = new ChannelMap(new WaveFormat(48000, 2, SampleType.Int16));
        var s = new short[2];
        fixed (short* p = s) i16.Encode([1f, -0.5f], 1, (IntPtr)p);
        Assert.Equal(32767, s[0]);
        Assert.Equal(-16384, s[1]);
    }

    [Fact]
    public void Extensible_format_is_parsed_from_native_struct()
    {
        // WAVEFORMATEXTENSIBLE float32 48 кГц 7.1, как отдаёт GetMixFormat
        var raw = new byte[40];
        BitConverter.GetBytes((ushort)0xFFFE).CopyTo(raw, 0);
        BitConverter.GetBytes((ushort)8).CopyTo(raw, 2);
        BitConverter.GetBytes(48000).CopyTo(raw, 4);
        BitConverter.GetBytes(48000 * 32).CopyTo(raw, 8);
        BitConverter.GetBytes((ushort)32).CopyTo(raw, 12);
        BitConverter.GetBytes((ushort)32).CopyTo(raw, 14);
        BitConverter.GetBytes((ushort)22).CopyTo(raw, 16);
        BitConverter.GetBytes((ushort)32).CopyTo(raw, 18);
        BitConverter.GetBytes(0x63F).CopyTo(raw, 20);
        new Guid("00000003-0000-0010-8000-00aa00389b71").TryWriteBytes(raw.AsSpan(24));
        IntPtr p = Marshal.AllocHGlobal(40);
        try
        {
            Marshal.Copy(raw, 0, p, 40);
            var f = WaveFormat.FromPointer(p);
            Assert.Equal(SampleType.Float32, f.Type);
            Assert.Equal(8, f.Channels);
            Assert.Equal(48000, f.SampleRate);
            Assert.Equal(0x63Fu, f.ChannelMask);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }
}

public class GainTests
{
    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(-6f, 0.501187f)]
    [InlineData(12f, 3.981072f)]
    [InlineData(20f, 3.981072f)]   // выше +12 обрезается
    [InlineData(-60f, 0f)]         // −60 и ниже — тишина
    [InlineData(-80f, 0f)]
    public void Db_scale(float db, float gain)
    {
        Assert.Equal(gain, Db.ToGain(db), 5);
    }

    [Fact]
    public void Gain_change_is_a_10ms_linear_ramp()
    {
        var g = new StereoGain();
        g.Init(48000, 1f, 1f, false);
        g.SetTargets(0f, 0f, false);
        var buf = new float[1000 * 2];
        Array.Fill(buf, 1f);
        g.Apply(buf, 1000);
        // 480 кадров рампы с равным шагом, дальше ноль
        Assert.Equal(1f - 1f / 480, buf[0], 5);
        Assert.Equal(0.5f, buf[2 * 239], 2);
        Assert.Equal(0f, buf[2 * 479], 5);
        Assert.Equal(0f, buf[2 * 999], 5);
        for (int i = 1; i < 480; i++) Assert.Equal(1f / 480, buf[2 * (i - 1)] - buf[2 * i], 4);
    }

    [Fact]
    public void Mute_is_a_5ms_fade()
    {
        var g = new StereoGain();
        g.Init(48000, 1f, 1f, false);
        g.SetTargets(1f, 1f, true);
        var buf = new float[400 * 2];
        Array.Fill(buf, 1f);
        g.Apply(buf, 400);
        Assert.True(buf[2 * 100] > 0.4f && buf[2 * 100] < 0.6f);
        Assert.Equal(0f, buf[2 * 239], 5);
        Assert.Equal(0f, buf[2 * 399], 5);
        Assert.True(g.IsSilent);
    }

    [Fact]
    public void Balance_attenuates_opposite_side()
    {
        var (l, r) = StereoGain.Targets(0, 0.5f);
        Assert.Equal(0.5f, l, 5);
        Assert.Equal(1f, r, 5);
        (l, r) = StereoGain.Targets(-6, -1f);
        Assert.Equal(0.501187f, l, 5);
        Assert.Equal(0f, r, 5);
    }

    [Fact]
    public void Limiter_holds_hot_signal_at_threshold()
    {
        var lim = new Limiter();
        lim.Init(48000);
        var buf = Signal.Sine(1000, 48000, 48000, amplitude: 2.0); // +6 dBFS
        float minGain = lim.Process(buf, 48000);
        float thr = MathF.Pow(10, -1 / 20f);
        float tailPeak = Signal.PeakAbs(buf.AsSpan(4800 * 2));
        Assert.True(minGain < 0.6f);
        Assert.True(tailPeak <= thr * 1.02f, $"пик {tailPeak}");
        Assert.True(Signal.PeakAbs(buf) <= 1f); // жёсткий потолок с первого отсчёта
    }

    [Fact]
    public void Limiter_does_not_touch_signal_below_threshold()
    {
        var lim = new Limiter();
        lim.Init(48000);
        var buf = Signal.Sine(1000, 48000, 4800, amplitude: 0.5);
        var copy = (float[])buf.Clone();
        Assert.Equal(1f, lim.Process(buf, 4800));
        Assert.Equal(copy, buf);
    }

    [Fact]
    public void Peak_meter_takes_and_resets()
    {
        var m = new PeakMeter();
        m.Update([0.1f, -0.7f, 0.3f, 0.2f], 2);
        m.Update([0.05f, 0.1f], 1);
        var (l, r) = m.Take();
        Assert.Equal(0.3f, l);
        Assert.Equal(0.7f, r);
        Assert.Equal((0f, 0f), m.Take());
    }
}
