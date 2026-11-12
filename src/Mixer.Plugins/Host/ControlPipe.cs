using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mixer.Plugins.Host;

/// <summary>
/// Канал управления, сторона микшера (сервер). Доступ — только текущему пользователю
/// (<see cref="PipeOptions.CurrentUserOnly"/>). Сообщение — длина (uint32, little-endian) и JSON в UTF-8.
/// </summary>
internal sealed class ControlPipe : IDisposable
{
    public const int MaxMessage = 64 << 20;

    private readonly NamedPipeServerStream _pipe;
    private readonly object _writeLock = new();
    private bool _disposed;

    public string Name { get; }
    public bool IsConnected => !_disposed && _pipe.IsConnected;

    public ControlPipe(string name)
    {
        Name = name;
        _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 1 << 16, 1 << 16);
    }

    public Task WaitForConnectionAsync(CancellationToken ct) => _pipe.WaitForConnectionAsync(ct);

    /// <summary>Читает сообщения, пока канал не закроется. Нечитаемое сообщение закрывает канал.</summary>
    public async Task ReadLoopAsync(Action<JsonObject> onMessage)
    {
        var header = new byte[4];
        try
        {
            while (true)
            {
                if (!await ReadExactAsync(header).ConfigureAwait(false)) return;
                int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (len < 0 || len > MaxMessage) return;
                var body = new byte[len];
                if (len > 0 && !await ReadExactAsync(body).ConfigureAwait(false)) return;
                if (JsonNode.Parse(body) is JsonObject msg) onMessage(msg);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or JsonException or OperationCanceledException)
        {
        }
    }

    private async Task<bool> ReadExactAsync(byte[] buffer)
    {
        int got = 0;
        while (got < buffer.Length)
        {
            int n = await _pipe.ReadAsync(buffer.AsMemory(got)).ConfigureAwait(false);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }

    /// <summary>false — канал закрыт.</summary>
    public bool Send(JsonObject msg)
    {
        byte[] body = Encoding.UTF8.GetBytes(msg.ToJsonString());
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        lock (_writeLock)
        {
            if (_disposed || !_pipe.IsConnected) return false;
            try
            {
                _pipe.Write(frame);
                return true;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _pipe.Dispose();
    }
}
