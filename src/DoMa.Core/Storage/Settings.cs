using DoMa.Core.Files;
using DoMa.Core.Models;

namespace DoMa.Core.Storage;

public class Settings
{
    int connectionsPerFile = 16;

    public string RootFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public Dictionary<Category, string[]> Extensions { get; set; } = CategoryResolver.Defaults.ToDictionary(p => p.Key, p => p.Value.ToArray());
    public int ConnectionsPerFile
    {
        get => connectionsPerFile;
        set => connectionsPerFile = Math.Clamp(value, 1, 32);
    }
    public int MaxConcurrent { get; set; } = 3;
    public bool RunAtStartup { get; set; }
    public bool NotifyOnComplete { get; set; } = true;
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Tốc độ tối đa cho tất cả lượt tải, KB/s (0 = không giới hạn).</summary>
    public int SpeedLimitKBps { get; set; }

    /// <summary>Hằng ngày tự bắt đầu các tệp "Chờ tải sau" vào giờ này (định dạng HH:mm).</summary>
    public bool ScheduleEnabled { get; set; }
    public string ScheduleStart { get; set; } = "23:00";

    /// <summary>Giờ tự tạm dừng các tệp đã bắt đầu theo lịch; rỗng = không dừng.</summary>
    public string? ScheduleStop { get; set; } = "07:00";
}
