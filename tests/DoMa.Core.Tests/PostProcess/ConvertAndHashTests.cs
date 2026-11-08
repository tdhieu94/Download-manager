using DoMa.Core.PostProcess;

namespace DoMa.Core.Tests.PostProcess;

public class ConvertAndHashTests
{
    [Theory]
    [InlineData("a.webm", true, true)]
    [InlineData("a.ts", true, true)]
    [InlineData("a.MKV", true, true)]
    [InlineData("a.mp4", false, true)]
    [InlineData("a.m4a", false, true)]
    [InlineData("a.mp3", false, false)]
    [InlineData("a.zip", false, false)]
    public void knows_what_converts(string name, bool mp4, bool mp3)
    {
        Assert.Equal(mp4, MediaConverter.CanConvertToMp4(name));
        Assert.Equal(mp3, MediaConverter.CanConvertToMp3(name));
    }

    [Fact]
    public void arguments_end_with_output()
    {
        Assert.Equal("o.mp4", MediaConverter.Mp4CopyArguments("i.webm", "o.mp4")[^1]);
        Assert.Contains("copy", MediaConverter.Mp4CopyArguments("i.webm", "o.mp4"));
        Assert.Contains("libx264", MediaConverter.Mp4EncodeArguments("i.webm", "o.mp4"));
        Assert.Contains("-vn", MediaConverter.Mp3Arguments("i.mp4", "o.mp3"));
    }

    [Fact]
    public void finds_hash_in_copied_text()
    {
        const string sha = "f6907a0af6cffb1797a87b645dd10bca08540369ab96634fb192c920bae2b4b0";
        Assert.Equal(sha, FileHasher.FindHash($"SHA256: {sha.ToUpperInvariant()}"));
        Assert.Equal(sha, FileHasher.FindHash($"{sha}  DoMa-Setup.exe"));
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", FileHasher.FindHash("md5=d41d8cd98f00b204e9800998ecf8427e"));
        Assert.Null(FileHasher.FindHash("https://example.com/file.zip"));
        Assert.Null(FileHasher.FindHash(null));
    }
}
