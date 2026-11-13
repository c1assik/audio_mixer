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
    private volatile bool _disposed;

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

    /// <summary>Столько ждать, пока допишется предыдущее сообщение (хост не читает канал — он завис).</summary>
    public static readonly TimeSpan WriteWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// false — канал закрыт или занят: прежняя запись стоит, потому что хост не читает. Ждать её бесконечно нельзя —
    /// пинг шёл бы из пула каждую секунду и занимал по потоку навсегда, а проверка зависания до хоста не доходила бы.
    /// </summary>
    public bool Send(JsonObject msg)
    {
        byte[] body = Encoding.UTF8.GetBytes(msg.ToJsonString());
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        if (!Monitor.TryEnter(_writeLock, WriteWait)) return false;
        try
        {
            if (_disposed || !_pipe.IsConnected) return false;
            _pipe.Write(frame);
            return true;
        }
        // OperationCanceled — канал закрыли, пока запись стояла
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            Monitor.Exit(_writeLock);
        }
    }

    public void Dispose()
    {
        // без блокировки записи: застрявшая запись держит её, пока канал не закроют
        if (_disposed) return;
        _disposed = true;
        _pipe.Dispose();
    }
}
