using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace DoMa.Core.Messaging;

public static class FrameCodec
{
    public const int MaxFrameBytes = 1024 * 1024;

    public static async Task WriteAsync(Stream s, JsonNode msg, CancellationToken ct)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(msg.ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        if (payload.Length > MaxFrameBytes)
            throw new InvalidDataException("Frame too large");
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        await s.WriteAsync(frame, ct);
        await s.FlushAsync(ct);
    }

    public static async Task<JsonNode?> ReadAsync(Stream s, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await TryReadExactAsync(s, header, ct))
            return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxFrameBytes)
            throw new InvalidDataException("Frame too large");
        var payload = new byte[length];
        if (!await TryReadExactAsync(s, payload, ct))
            throw new EndOfStreamException();
        return JsonNode.Parse(payload);
    }

    static async Task<bool> TryReadExactAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                if (read == 0)
                    return false;
                throw new EndOfStreamException();
            }
            read += n;
        }
        return true;
    }
}
