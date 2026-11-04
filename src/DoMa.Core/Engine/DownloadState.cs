using System.Text.Json;

namespace DoMa.Core.Engine;

public class Segment
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Downloaded { get; set; }
}

public class DownloadState
{
    public long? Size { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public bool SupportsRange { get; set; }
    public List<Segment> Segments { get; set; } = [];

    public static DownloadState? Load(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<DownloadState>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, path, true);
    }
}
