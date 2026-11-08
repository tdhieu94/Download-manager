using DoMa.Core.Models;
using DoMa.Core.Queue;

namespace DoMa.Core.Tests.Queue;

public class DuplicatesTests
{
    private static DownloadItem Item(string url, string path, DownloadStatus status, long? size = null, int day = 1) => new()
    {
        Id = Guid.NewGuid(),
        Request = new DownloadRequest(url, null, null, null, null, null, null),
        TargetPath = path,
        Status = status,
        TotalBytes = size,
        AddedAt = new DateTimeOffset(2026, 11, day, 0, 0, 0, TimeSpan.Zero),
        CompletedAt = status == DownloadStatus.Completed ? new DateTimeOffset(2026, 11, day, 1, 0, 0, TimeSpan.Zero) : null,
    };

    [Fact]
    public void same_link_ignoring_fragment_and_case_of_host()
    {
        var done = Item("https://Example.com/a.zip", "C:/d/a.zip", DownloadStatus.Completed);
        Assert.Same(done, Duplicates.Find([done], "https://example.com/a.zip#top", null, "a.zip", null));
    }

    [Fact]
    public void same_name_and_size_from_another_link()
    {
        var done = Item("https://cdn1/x?token=1", "C:/d/Setup (2).exe", DownloadStatus.Completed, 5_000_000);
        Assert.Same(done, Duplicates.Find([done], "https://cdn2/x?token=2", null, "setup.exe", 5_000_000));
        Assert.Null(Duplicates.Find([done], "https://cdn2/x?token=2", null, "setup.exe", 4_000_000));
        Assert.Null(Duplicates.Find([done], "https://cdn2/x?token=2", null, "setup.exe", null));
    }

    [Fact]
    public void prefers_unfinished_then_newest()
    {
        var old = Item("https://h/a", "C:/d/a", DownloadStatus.Completed, day: 1);
        var recent = Item("https://h/a", "C:/d/a (2)", DownloadStatus.Completed, day: 5);
        Assert.Same(recent, Duplicates.Find([old, recent], "https://h/a", null, "a", null));
        var paused = Item("https://h/a", "C:/d/a (3)", DownloadStatus.Paused, day: 2);
        Assert.Same(paused, Duplicates.Find([old, recent, paused], "https://h/a", null, "a", null));
    }

    [Fact]
    public void different_file_is_not_a_duplicate()
    {
        var done = Item("https://h/a.zip", "C:/d/a.zip", DownloadStatus.Completed, 10);
        Assert.Null(Duplicates.Find([done], "https://h/b.zip", null, "b.zip", 10));
    }
}
