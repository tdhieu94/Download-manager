using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using DoMa.Core.Messaging;

namespace DoMa.App.Ipc;

public sealed class PipeServer(string pipeName)
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    public event Action<PipeConnection, JsonNode>? MessageReceived;

    public static string CurrentUserPipeName => $"DoMa.Pipe.{CurrentUserSid().Value}";

    public Task StartAsync(CancellationToken ct)
    {
        var first = CreateInstance(PipeOptions.FirstPipeInstance);
        _ = AcceptLoopAsync(first, ct);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream? server, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                server ??= CreateInstance(PipeOptions.None);
                await server.WaitForConnectionAsync(ct);
                _ = ServeAsync(server, ct);
                server = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                if (server is not null)
                    await server.DisposeAsync();
                server = null;
                try
                {
                    await Task.Delay(RetryDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        if (server is not null)
            await server.DisposeAsync();
    }

    private async Task ServeAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        await using (stream)
        {
            var connection = new PipeConnection(stream);
            try
            {
                while (await FrameCodec.ReadAsync(stream, ct) is { } msg)
                    Dispatch(connection, msg);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                connection.MarkClosed();
            }
        }
    }

    private void Dispatch(PipeConnection connection, JsonNode msg)
    {
        try
        {
            MessageReceived?.Invoke(connection, msg);
        }
        catch (Exception)
        {
        }
    }

    private NamedPipeServerStream CreateInstance(PipeOptions extra)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(CurrentUserSid(), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | extra,
            0,
            0,
            security);
    }

    private static SecurityIdentifier CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }
}
