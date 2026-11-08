using DoMa.Core.Engine.YouTube;
using DoMa.Core.Models;
using DoMa.Core.Storage;

namespace DoMa.Core.Channels;

/// <summary>Kênh hoặc playlist YouTube đang theo dõi: DoMa định kỳ xem có video mới thì tự tải.</summary>
public sealed class FollowedChannel
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Quality { get; set; } = MediaOptions.Best;
    public bool Subtitles { get; set; }

    /// <summary>Link các video đã thấy (đã tải hoặc có sẵn lúc bắt đầu theo dõi), mới nhất trước.</summary>
    public List<string> Seen { get; set; } = [];

    public DateTimeOffset? LastCheck { get; set; }

    public MediaOptions Options => new(Quality, Subtitles);
}

public static class ChannelWatch
{
    public const int MaxSeen = 2000;

    /// <summary>Kênh: chỉ cần xem vài video mới nhất. Playlist: video mới thường nằm cuối nên đọc cả danh sách.</summary>
    public static int? CheckLimit(string url) => url.Contains("/playlist?", StringComparison.Ordinal) ? null : 30;

    public static readonly TimeSpan Interval = TimeSpan.FromHours(3);

    public static bool Due(FollowedChannel channel, DateTimeOffset now) => channel.LastCheck is not { } last || now - last >= Interval;

    /// <summary>Video chưa thấy bao giờ, theo thứ tự trong danh sách.</summary>
    public static List<PlaylistEntry> NewEntries(FollowedChannel channel, IEnumerable<PlaylistEntry> latest)
    {
        var seen = channel.Seen.ToHashSet(StringComparer.Ordinal);
        return latest.Where(e => seen.Add(e.Url)).ToList();
    }

    public static void MarkSeen(FollowedChannel channel, IEnumerable<string> urls)
    {
        var fresh = urls.Where(u => !channel.Seen.Contains(u)).ToList();
        channel.Seen.InsertRange(0, fresh);
        if (channel.Seen.Count > MaxSeen)
            channel.Seen.RemoveRange(MaxSeen, channel.Seen.Count - MaxSeen);
    }
}

public sealed class ChannelStore(string dataDir)
{
    private string FilePath => Path.Combine(dataDir, "channels.json");

    public List<FollowedChannel> Load()
    {
        try
        {
            return JsonFile.Read<List<FollowedChannel>>(FilePath) ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    public void Save(List<FollowedChannel> channels) => JsonFile.Write(FilePath, channels);
}
