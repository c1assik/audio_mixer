using System.Buffers.Binary;
using System.Text;

namespace Mixer.Probe.Audio;

public sealed class WavWriter : IDisposable
{
    private readonly FileStream _fs;
    private readonly long _dataSizeOffset;
    private long _dataBytes;

    public string Path { get; }

    public WavWriter(string path, AudioFormat format)
    {
        Path = path;
        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        var fmt = format.FmtChunk;
        WriteAscii("RIFF");
        WriteUInt32(0);
        WriteAscii("WAVE");
        WriteAscii("fmt ");
        WriteUInt32((uint)fmt.Length);
        _fs.Write(fmt);
        WriteAscii("data");
        _dataSizeOffset = _fs.Position;
        WriteUInt32(0);
    }

    public void Write(byte[] buffer, int count)
    {
        _fs.Write(buffer, 0, count);
        _dataBytes += count;
    }

    public void Dispose()
    {
        if ((_dataBytes & 1) == 1) _fs.WriteByte(0);
        long length = _fs.Length;
        _fs.Position = 4;
        WriteUInt32((uint)Math.Min(uint.MaxValue, length - 8));
        _fs.Position = _dataSizeOffset;
        WriteUInt32((uint)Math.Min(uint.MaxValue, _dataBytes));
        _fs.Dispose();
    }

    private void WriteAscii(string s) => _fs.Write(Encoding.ASCII.GetBytes(s));

    private void WriteUInt32(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        _fs.Write(b);
    }
}
