using System.IO.Compression;
using System.Text;
using DoMa.Core.PostProcess;

namespace DoMa.Core.Tests.PostProcess;

public sealed class PostProcessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DoMaTests", Guid.NewGuid().ToString("N"));

    public PostProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task hashes_known_content()
    {
        var file = Path.Combine(_dir, "abc.txt");
        File.WriteAllText(file, "abc");
        var h = await FileHasher.ComputeAsync(file, null, CancellationToken.None);
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", h.Md5);
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", h.Sha1);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", h.Sha256);
        Assert.Equal("SHA-256", FileHasher.Match("  BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD  abc.txt", h));
        Assert.Equal("MD5", FileHasher.Match("900150983cd24fb0d6963f7d28e17f72", h));
        Assert.Null(FileHasher.Match("deadbeef", h));
    }

    [Fact]
    public void extracts_zip_next_to_file_without_overwriting()
    {
        var zip = Path.Combine(_dir, "Tài liệu.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("a/b.txt").Open(), Encoding.UTF8))
                w.Write("hello");
        }
        Directory.CreateDirectory(Path.Combine(_dir, "Tài liệu"));

        var target = ArchiveExtractor.Extract(zip);

        Assert.Equal(Path.Combine(_dir, "Tài liệu (1)"), target);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "a", "b.txt")));
    }

    [Fact]
    public void rejects_zip_slip()
    {
        var zip = Path.Combine(_dir, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            archive.CreateEntry("../escape.txt");

        Assert.Throws<InvalidDataException>(() => ArchiveExtractor.Extract(zip));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "escape.txt")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "evil")));
    }
}
