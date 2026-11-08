using System.Buffers.Binary;
using System.Text;
using DoMa.Core.PostProcess;

namespace DoMa.Core.Tests.PostProcess;

public sealed class TsToMp4Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DoMaMp4", Guid.NewGuid().ToString("N"));

    public TsToMp4Tests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void converts_h264_aac_to_mp4()
    {
        var output = Path.Combine(_dir, "out.mp4");
        TsToMp4.Convert(Fixture("sample.ts"), output);

        var boxes = TopLevel(File.ReadAllBytes(output));
        Assert.Equal(["ftyp", "mdat", "moov"], boxes.Select(b => b.Type));
        var moov = boxes[2].Body;
        Assert.Equal(2, Count(moov, "trak"));
        Assert.Contains("avcC", Encoding.ASCII.GetString(moov));
        Assert.Contains("esds", Encoding.ASCII.GetString(moov));
        // 2 giây ở 25 khung/giây.
        Assert.Contains(50u, SampleCounts(moov));
    }

    [Fact]
    public void converts_video_only()
    {
        var output = Path.Combine(_dir, "v.mp4");
        TsToMp4.Convert(Fixture("video-only.ts"), output);
        var moov = TopLevel(File.ReadAllBytes(output))[2].Body;
        Assert.Equal(1, Count(moov, "trak"));
        Assert.Contains(10u, SampleCounts(moov));
    }

    [Fact]
    public void non_ts_input_fails_and_leaves_no_output()
    {
        var input = Path.Combine(_dir, "x.ts");
        File.WriteAllBytes(input, new byte[188 * 4]);
        var output = Path.Combine(_dir, "x.mp4");
        Assert.Throws<NotSupportedException>(() => TsToMp4.Convert(input, output));
        Assert.False(File.Exists(output));
    }

    private static List<(string Type, byte[] Body)> TopLevel(byte[] data)
    {
        var result = new List<(string, byte[])>();
        var i = 0;
        while (i + 8 <= data.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i));
            var type = Encoding.ASCII.GetString(data, i + 4, 4);
            var header = 8;
            if (size == 1)
            {
                size = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(i + 8));
                header = 16;
            }
            result.Add((type, data[(i + header)..(i + (int)size)]));
            i += (int)size;
        }
        return result;
    }

    private static int Count(byte[] data, string type)
    {
        var text = Encoding.ASCII.GetString(data);
        var count = 0;
        for (var i = text.IndexOf(type, StringComparison.Ordinal); i >= 0; i = text.IndexOf(type, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>Số mẫu trong từng stsz.</summary>
    private static List<uint> SampleCounts(byte[] moov)
    {
        var text = Encoding.ASCII.GetString(moov);
        var result = new List<uint>();
        for (var i = text.IndexOf("stsz", StringComparison.Ordinal); i >= 0; i = text.IndexOf("stsz", i + 1, StringComparison.Ordinal))
            result.Add(BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(i + 12)));
        return result;
    }
}
