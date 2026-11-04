using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using DoMa.App.Ipc;
using DoMa.Core.Messaging;

namespace DoMa.App.Tests;

public sealed class PipeServerTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _name = $"DoMa.Test.{Guid.NewGuid():N}";
    private readonly CancellationTokenSource _cts = new();

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task<PipeServer> StartEchoServerAsync()
    {
        var server = new PipeServer(_name);
        server.MessageReceived += (connection, msg) =>
            _ = connection.SendAsync(new JsonObject { ["type"] = MessageTypes.Pong, ["id"] = msg["id"]?.DeepClone() });
        await server.StartAsync(_cts.Token);
        return server;
    }

    private async Task<NamedPipeClientStream> ConnectAsync()
    {
        var client = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        return client;
    }

    private static Task SendAsync(Stream s, string id) =>
        FrameCodec.WriteAsync(s, new JsonObject { ["type"] = MessageTypes.Ping, ["id"] = id }, CancellationToken.None);

    private static Task<JsonNode?> ReadAsync(Stream s) =>
        FrameCodec.ReadAsync(s, CancellationToken.None).WaitAsync(Timeout);

    [Fact]
    public async Task pipe_roundtrip()
    {
        await StartEchoServerAsync();
        await using var client = await ConnectAsync();

        await SendAsync(client, "1");
        var reply = await ReadAsync(client);

        Assert.Equal(MessageTypes.Pong, (string?)reply!["type"]);
        Assert.Equal("1", (string?)reply["id"]);
    }

    [Fact]
    public async Task two_clients_get_own_replies()
    {
        await StartEchoServerAsync();
        await using var a = await ConnectAsync();
        await using var b = await ConnectAsync();

        await SendAsync(b, "b");
        await SendAsync(a, "a");

        Assert.Equal("a", (string?)(await ReadAsync(a))!["id"]);
        Assert.Equal("b", (string?)(await ReadAsync(b))!["id"]);
    }

    [Fact]
    public async Task malformed_frame_closes_only_that_connection()
    {
        await StartEchoServerAsync();
        await using var bad = await ConnectAsync();
        await using var good = await ConnectAsync();

        var payload = Encoding.UTF8.GetBytes("{not json");
        await bad.WriteAsync(BitConverter.GetBytes(payload.Length));
        await bad.WriteAsync(payload);
        await bad.FlushAsync();

        Assert.Null(await ReadAsync(bad));
        await SendAsync(good, "g");
        Assert.Equal("g", (string?)(await ReadAsync(good))!["id"]);
        await using var later = await ConnectAsync();
        await SendAsync(later, "l");
        Assert.Equal("l", (string?)(await ReadAsync(later))!["id"]);
    }

    [Fact]
    public async Task connection_reports_disconnect_and_send_fails()
    {
        var captured = new TaskCompletionSource<PipeConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new PipeServer(_name);
        server.MessageReceived += (connection, _) => captured.TrySetResult(connection);
        await server.StartAsync(_cts.Token);
        var client = await ConnectAsync();
        await SendAsync(client, "1");
        var connection = await captured.Task.WaitAsync(Timeout);

        Assert.True(connection.IsConnected);
        var read = ReadAsync(client);
        Assert.True(await connection.SendAsync(new JsonObject { ["type"] = MessageTypes.Pong }));
        Assert.Equal(MessageTypes.Pong, (string?)(await read)!["type"]);

        await client.DisposeAsync();
        var deadline = DateTime.UtcNow + Timeout;
        while (connection.IsConnected && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.False(connection.IsConnected);
        Assert.False(await connection.SendAsync(new JsonObject { ["type"] = MessageTypes.Pong }));
    }

    [Fact]
    public async Task second_server_on_same_name_fails()
    {
        await StartEchoServerAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new PipeServer(_name).StartAsync(_cts.Token));
    }

    [Fact]
    public async Task accept_loop_survives_client_that_disconnects_immediately()
    {
        await StartEchoServerAsync();
        for (var i = 0; i < 3; i++)
        {
            var dropped = await ConnectAsync();
            await dropped.DisposeAsync();
        }

        await using var client = await ConnectAsync();
        await SendAsync(client, "after");
        Assert.Equal("after", (string?)(await ReadAsync(client))!["id"]);
    }

    [Fact]
    public async Task handler_exception_keeps_connection_open()
    {
        var server = new PipeServer(_name);
        server.MessageReceived += (connection, msg) =>
        {
            if ((string?)msg["id"] == "boom")
                throw new InvalidOperationException();
            _ = connection.SendAsync(new JsonObject { ["type"] = MessageTypes.Pong, ["id"] = msg["id"]?.DeepClone() });
        };
        await server.StartAsync(_cts.Token);
        await using var client = await ConnectAsync();

        await SendAsync(client, "boom");
        await SendAsync(client, "ok");

        Assert.Equal("ok", (string?)(await ReadAsync(client))!["id"]);
    }

    [Fact]
    public async Task verified_client_connects_to_current_user_server()
    {
        await StartEchoServerAsync();

        await using var client = await PipeClient.ConnectAsync(_name, 5000, CancellationToken.None);
        await SendAsync(client, "v");

        Assert.Equal("v", (string?)(await ReadAsync(client))!["id"]);
    }

    [Fact]
    public async Task verified_client_times_out_when_no_server()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => PipeClient.ConnectAsync(_name, 200, CancellationToken.None));
    }
}
