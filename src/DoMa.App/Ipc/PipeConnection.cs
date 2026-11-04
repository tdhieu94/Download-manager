using System.Text.Json.Nodes;
using DoMa.Core.Messaging;

namespace DoMa.App.Ipc;

public sealed class PipeConnection
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _closed;

    internal PipeConnection(Stream stream) => _stream = stream;

    public bool IsConnected => !_closed;

    internal void MarkClosed() => _closed = true;

    public async Task<bool> SendAsync(JsonNode msg)
    {
        if (_closed)
            return false;
        using var timeout = new CancellationTokenSource(SendTimeout);
        try
        {
            await _writeLock.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            MarkClosed();
            _stream.Dispose();
            return false;
        }
        try
        {
            await FrameCodec.WriteAsync(_stream, msg, timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            MarkClosed();
            _stream.Dispose();
            return false;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            MarkClosed();
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
