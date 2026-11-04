using DoMa.Core.Files;

namespace DoMa.Core.Tests.Files;

public class FileNameResolverTests
{
    [Fact]
    public void sanitizes_invalid_characters() =>
        Assert.Equal("Báo cáo_ Q3_.pdf", FileNameResolver.Sanitize("Báo cáo: Q3?.pdf"));

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
