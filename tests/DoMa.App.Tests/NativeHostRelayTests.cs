using System.IO.Pipes;
using System.Text.Json.Nodes;
using DoMa.App.Hosting;
using DoMa.Core.Messaging;

namespace DoMa.App.Tests;

public sealed class NativeHostRelayTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _name = $"DoMa.Test.{Guid.NewGuid():N}";
    private readonly AnonymousPipeServerStream _stdinWriter = new(PipeDirection.Out);
    private readonly AnonymousPipeClientStream _stdin;
    private readonly AnonymousPipeServerStream _stdoutReader = new(PipeDirection.In);
    private readonly AnonymousPipeClientStream _stdout;
    private readonly NamedPipeServerStream _app;

    public NativeHostRelayTests()
    {
        _stdin = new(PipeDirection.In, _stdinWriter.ClientSafePipeHandle);
        _stdout = new(PipeDirection.Out, _stdoutReader.ClientSafePipeHandle);
        _app = new(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    public async ValueTask DisposeAsync()
    {
        await _stdinWriter.DisposeAsync();
        await _stdin.DisposeAsync();
        await _stdout.DisposeAsync();
        await _stdoutReader.DisposeAsync();
        await _app.DisposeAsync();
    }

    private async Task<Stream> ConnectAsync(CancellationToken ct)
    {
        var client = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, ct);
        return client;
    }

    private NativeHostRelay Relay(Func<CancellationToken, Task<Stream>>? connect = null) =>
        new(_stdin, _stdout, connect ?? ConnectAsync);

    private static Task WriteAsync(Stream s, string type, string id) =>
        FrameCodec.WriteAsync(s, new JsonObject { ["type"] = type, ["id"] = id }, CancellationToken.None);

    private static Task<JsonNode?> ReadAsync(Stream s) =>
        FrameCodec.ReadAsync(s, CancellationToken.None).WaitAsync(Timeout);

    [Fact]
    public async Task relay_forwards_both_directions()
    {
        var run = Relay().RunAsync(CancellationToken.None);
        await _app.WaitForConnectionAsync().WaitAsync(Timeout);

        await WriteAsync(_stdinWriter, MessageTypes.Ping, "p");
        var pong = await ReadAsync(_stdoutReader);
        Assert.Equal(MessageTypes.Pong, (string?)pong!["type"]);
        Assert.Matches(@"^\d+\.\d+\.\d+$", (string?)pong["version"]);

        await WriteAsync(_stdinWriter, MessageTypes.Capture, "1");
        var atApp = await ReadAsync(_app);
        Assert.Equal(MessageTypes.Capture, (string?)atApp!["type"]);
        Assert.Equal("1", (string?)atApp["id"]);

        await WriteAsync(_app, MessageTypes.Accepted, "1");
        var atBrowser = await ReadAsync(_stdoutReader);
        Assert.Equal(MessageTypes.Accepted, (string?)atBrowser!["type"]);
        Assert.Equal("1", (string?)atBrowser["id"]);

        _stdinWriter.Dispose();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task relay_exits_on_stdin_eof()
    {
        var run = Relay().RunAsync(CancellationToken.None);
        await _app.WaitForConnectionAsync().WaitAsync(Timeout);

        _stdinWriter.Dispose();

        await run.WaitAsync(Timeout);
        Assert.Null(await ReadAsync(_app));
    }

    [Fact]
    public async Task relay_pong_only_when_pipe_connected()
    {
        await WriteAsync(_stdinWriter, MessageTypes.Ping, "p");

        await Relay(_ => throw new TimeoutException()).RunAsync(CancellationToken.None).WaitAsync(Timeout);

        _stdout.Dispose();
        Assert.Null(await ReadAsync(_stdoutReader));
    }

    [Fact]
    public async Task relay_exits_on_malformed_stdin()
    {
        var run = Relay().RunAsync(CancellationToken.None);
        await _app.WaitForConnectionAsync().WaitAsync(Timeout);

        await _stdinWriter.WriteAsync(BitConverter.GetBytes(FrameCodec.MaxFrameBytes + 1));

        await run.WaitAsync(Timeout);
    }

    [Theory]
    [InlineData(new[] { "chrome-extension://abcdefghijklmnop/" }, true)]
    [InlineData(new[] { "chrome-extension://abcdefghijklmnop/", "--parent-window=0" }, true)]
    [InlineData(new[] { "--nen" }, false)]
    [InlineData(new string[0], false)]
    public void host_mode_detected_from_caller_origin(string[] args, bool expected) =>
        Assert.Equal(expected, NativeHostRelay.IsHostInvocation(args));
}
