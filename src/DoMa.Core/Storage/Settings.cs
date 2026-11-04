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
}
