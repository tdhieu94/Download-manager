using DoMa.Core.Files;
using DoMa.Core.Models;

namespace DoMa.Core.Tests.Files;

public class CategoryResolverTests
{
    [Theory]
    [InlineData("Win10_22H2_x64.iso", Category.Software)]
    [InlineData("bao-cao.PDF", Category.Document)]
    [InlineData("data.tar.gz", Category.Archive)]
    [InlineData("noextension", Category.Other)]
    [InlineData("Phim hay.MP4", Category.Media)]
    [InlineData("video.ts", Category.Media)]
    [InlineData("bai-hat.flac", Category.Media)]
    public void resolves_category(string name, Category expected) =>
        Assert.Equal(expected, new CategoryResolver(CategoryResolver.Defaults, @"C:\D").Resolve(name));

    [Fact]
    public void media_goes_to_video_folder() =>
        Assert.Equal(Path.Combine("/d", "Video"), new CategoryResolver(CategoryResolver.Defaults, "/d").FolderFor(Category.Media));

    [Fact]
    public void folder_for_software() =>
        Assert.Equal(@"C:\D\PhanMem", new CategoryResolver(CategoryResolver.Defaults, @"C:\D").FolderFor(Category.Software));
}
