using DoMa.Core.Files;

namespace DoMa.Core.Tests.Files;

public class DiskSpaceTests
{
    private static readonly string Path1 = Path.Combine(Path.GetTempPath(), "x.bin");

    [Fact]
    public void reports_shortage_with_sizes()
    {
        var message = DiskSpace.Shortage(Path1, 2L << 30, _ => 1L << 30);
        Assert.NotNull(message);
        Assert.Contains("cần 2,0 GB", message);
        Assert.Contains("chỉ còn 1,0 GB", message);
    }

    [Fact]
    public void keeps_a_small_reserve()
    {
        Assert.NotNull(DiskSpace.Shortage(Path1, 100L << 20, _ => (100L << 20) + 1));
        Assert.Null(DiskSpace.Shortage(Path1, 100L << 20, _ => (100L << 20) + DiskSpace.Reserve));
    }

    [Fact]
    public void unknown_size_or_free_space_is_not_a_shortage()
    {
        Assert.Null(DiskSpace.Shortage(Path1, null, _ => 0));
        Assert.Null(DiskSpace.Shortage(Path1, 0, _ => 0));
        Assert.Null(DiskSpace.Shortage(Path1, 1L << 30, _ => null));
    }

    [Fact]
    public void real_drive_free_space_is_readable()
    {
        Assert.True(DiskSpace.FreeBytes(Path.GetTempPath()) > 0);
    }
}
