using System.Text.Json;
using System.Text.Json.Nodes;
using DoMa.Core.Messaging;

namespace DoMa.App.Hosting;

public sealed class NativeHostRelay(Stream stdin, Stream stdout, Func<CancellationToken, Task<Stream>> connectPipe)
{
    private readonly SemaphoreSlim _stdoutLock = new(1, 1);

    /// <summary>Phiên bản DoMa (x.y.z) gửi kèm "pong" để tiện ích biết khi nào cần tự nạp lại.</summary>
    public static string AppVersion { get; } = Normalize(typeof(NativeHostRelay).Assembly.GetName().Version);

    private static string Normalize(Version? v) => v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

    public static bool IsHostInvocation(string[] args) =>
        args.Length > 0 && args[0].StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);

    public async Task RunAsync(CancellationToken ct)
    {
        Stream pipe;
        try
        {
            pipe = await connectPipe(ct);
        }
        catch (Exception)
        {
            return;
        }

        await using (pipe)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var inbound = PumpStdinAsync(pipe, cts.Token);
            var outbound = PumpPipeAsync(pipe, cts.Token);
            await Task.WhenAny(inbound, outbound);
            cts.Cancel();
        }
    }

    private async Task PumpStdinAsync(Stream pipe, CancellationToken ct)
    {
        try
        {
            while (await FrameCodec.ReadAsync(stdin, ct) is { } msg)
            {
                if (Messages.TypeOf(msg) == MessageTypes.Ping)
                    await WriteStdoutAsync(new JsonObject { ["type"] = MessageTypes.Pong, ["version"] = AppVersion }, ct);
                else
                    await FrameCodec.WriteAsync(pipe, msg, ct);
            }
        }
        catch (Exception e) when (IsConnectionFailure(e))
        {
        }
    }

    private async Task PumpPipeAsync(Stream pipe, CancellationToken ct)
    {
        try
        {
            while (await FrameCodec.ReadAsync(pipe, ct) is { } msg)
                await WriteStdoutAsync(msg, ct);
        }
        catch (Exception e) when (IsConnectionFailure(e))
        {
        }
    }

    private async Task WriteStdoutAsync(JsonNode msg, CancellationToken ct)
    {
        await _stdoutLock.WaitAsync(ct);
        try
        {
            await FrameCodec.WriteAsync(stdout, msg, ct);
        }
        finally
        {
            _stdoutLock.Release();
        }
    }

    private static bool IsConnectionFailure(Exception e) =>
        e is IOException or InvalidDataException or JsonException or OperationCanceledException or ObjectDisposedException;
}
