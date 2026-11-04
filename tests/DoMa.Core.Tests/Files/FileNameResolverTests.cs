using DoMa.Core.Files;

namespace DoMa.Core.Tests.Files;

public class FileNameResolverTests
{
    [Fact]
    public void sanitizes_invalid_characters() =>
        Assert.Equal("Báo cáo_ Q3_.pdf", FileNameResolver.Sanitize("Báo cáo: Q3?.pdf"));

    [Theory]
    [InlineData("T\u00C3\u00A0i li\u00E1\u00BB\u0087u h\u00E1\u00BB\u008Dc t\u00E1\u00BA\u00ADp.pdf", "Tài liệu học tập.pdf")]
    [InlineData("Ph%E1%BA%A7n%20m%E1%BB%81m.exe", "Phần mềm.exe")]
    [InlineData("Ba\u0300i ta\u0323\u0302p.docx", "Bài tập.docx")]
    [InlineData("café.pdf", "café.pdf")]
    [InlineData("Tài liệu.pdf", "Tài liệu.pdf")]
    [InlineData("100% done.zip", "100% done.zip")]
    public void repairs_vietnamese_names(string input, string expected) =>
        Assert.Equal(expected, FileNameResolver.Sanitize(input));

    [Fact]
    public void sanitize_empty_becomes_download() =>
        Assert.Equal("download", FileNameResolver.Sanitize("  .. "));

    [Fact]
    public void make_unique_appends_counter()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\D\a.iso", @"C:\D\a (1).iso" };
        Assert.Equal(@"C:\D\a (2).iso", FileNameResolver.MakeUnique(@"C:\D\a.iso", taken.Contains));
    }
}
