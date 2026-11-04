using System.Text.Json.Nodes;
using DoMa.Core.Messaging;

namespace DoMa.Core.Tests.Messaging;

public class FrameCodecTests
{
    [Fact]
    public async Task roundtrip_preserves_unicode()
    {
        var stream = new MemoryStream();
        var msg = JsonNode.Parse("""{"type":"bat","filename":"Tài liệu.pdf"}""")!;
        await FrameCodec.WriteAsync(stream, msg, CancellationToken.None);
        stream.Position = 0;
        var read = await FrameCodec.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(msg.ToJsonString(), read!.ToJsonString());
        Assert.Equal("Tài liệu.pdf", (string?)read["filename"]);
    }

    [Fact]
    public async Task length_prefix_is_little_endian()
    {
        var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, new JsonObject(), CancellationToken.None);
        Assert.Equal(new byte[] { 2, 0, 0, 0 }, stream.ToArray().Take(4).ToArray());
    }

    [Fact]
    public async Task eof_returns_null() =>
        Assert.Null(await FrameCodec.ReadAsync(new MemoryStream(), CancellationToken.None));

    [Fact]
    public async Task oversized_frame_throws()
    {
        var stream = new MemoryStream(BitConverter.GetBytes(1024 * 1024 + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameCodec.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void capture_message_maps_final_url()
    {
        var node = JsonNode.Parse("""{"id":"1","url":"http://a/x","finalUrl":"http://b/y","filename":"f.zip","fileSize":42}""")!;
        var request = CaptureMessage.From(node).ToRequest();
        Assert.Equal("http://b/y", request.Url);
        Assert.Equal("f.zip", request.SuggestedFileName);
        Assert.Equal(42, request.SizeHint);
    }

    [Fact]
    public void reply_has_type_and_id() =>
        Assert.Equal("""{"type":"nhan","id":"7"}""", Messages.Reply(MessageTypes.Accepted, "7").ToJsonString());

    [Theory]
    [InlineData("""{"type":"ping"}""", "ping")]
    [InlineData("""{"type":1}""", null)]
    [InlineData("""[1]""", null)]
    [InlineData("""42""", null)]
    public void type_of_tolerates_any_json(string json, string? expected) =>
        Assert.Equal(expected, Messages.TypeOf(JsonNode.Parse(json)!));
}
