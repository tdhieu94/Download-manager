using DoMa.Core.Engine.Torrent;

namespace DoMa.Core.Tests.Engine;

public class TorrentTests
{
    private const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Ubuntu+24.04+Desktop&tr=udp%3A%2F%2Ftracker";

    [Fact]
    public void recognizes_torrent_links()
    {
        Assert.True(TorrentLink.IsMagnet(Magnet));
        Assert.True(TorrentLink.IsTorrent("https://example.com/files/linux.torrent"));
        Assert.True(TorrentLink.IsTorrent("https://example.com/dl?id=1", "application/x-bittorrent"));
        Assert.False(TorrentLink.IsTorrent("https://example.com/a.zip"));
        Assert.False(TorrentLink.IsMagnet("magnet:?xt=urn:sha1:abc"));
    }

    [Fact]
    public void display_name()
    {
        Assert.Equal("Ubuntu 24.04 Desktop", TorrentLink.DisplayName(Magnet));
        Assert.Equal("Torrent 01234567", TorrentLink.DisplayName("magnet:?xt=urn:btih:0123456789abcdef"));
        Assert.Equal("linux", TorrentLink.DisplayName("https://example.com/files/linux.torrent"));
    }

    [Fact]
    public void parses_progress_lines()
    {
        var p = Aria2.ParseProgress("[#2089b0 3500000B/33000000B(10%) CN:8 SD:3 DL:1200000B ETA:20s]");
        Assert.Equal((3500000L, (long?)33000000L, (int?)3), p);
        var human = Aria2.ParseProgress("[#2089b0 1.5MiB/2.0GiB(0%) CN:1 DL:0B]");
        Assert.Equal((long)(1.5 * 1024 * 1024), human!.Value.Done);
        Assert.Equal(2L << 30, human.Value.Total);
        Assert.Null(Aria2.ParseProgress("[#2089b0 0B/0B CN:0 DL:0B]")!.Value.Total);
        Assert.Null(Aria2.ParseProgress("11/08 14:00:00 [NOTICE] Downloading 1 item(s)"));
    }

    [Fact]
    public void finds_completed_top_entry()
    {
        Assert.Null(Aria2.ParseComplete("[NOTICE] Download complete: [MEMORY][METADATA]Ubuntu"));
        var file = Aria2.ParseComplete("11/08 14:00:00 [NOTICE] Download complete: " + Path.Combine("D", "Khac", "Ubuntu", "a.iso"));
        Assert.Equal(Path.Combine("D", "Khac", "Ubuntu"), Aria2.TopEntry(Path.Combine("D", "Khac"), file!));
        Assert.Equal(Path.Combine("D", "Khac", "one.iso"), Aria2.TopEntry(Path.Combine("D", "Khac"), Path.Combine("D", "Khac", "one.iso")));
    }

    [Fact]
    public void arguments_never_seed_and_pass_cookies_for_torrent_files_only()
    {
        var args = Aria2.Arguments("https://t.example/x.torrent", "dir", "a=1", "https://t.example/", 1024);
        Assert.Contains("--seed-time=0", args);
        Assert.Contains("--header=Cookie: a=1", args);
        Assert.Contains("--max-overall-download-limit=1024", args);
        Assert.Equal("https://t.example/x.torrent", args[^1]);
        Assert.DoesNotContain(Aria2.Arguments(Magnet, "dir", "a=1", null), a => a.StartsWith("--header", StringComparison.Ordinal));
        Assert.True(Aria2.DescribeExit(2, []).Network);
    }
}
