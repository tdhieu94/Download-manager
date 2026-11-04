using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DoMa.Core.Storage;

static class JsonFile
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    static readonly ConcurrentDictionary<string, object> Gates = new();

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var gate = Gates.GetOrAdd(Path.GetFullPath(path), _ => new object());
        lock (gate)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, true);
        }
    }

    public static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (JsonException)
        {
            File.Move(path, path + ".bad", true);
            return null;
        }
    }
}
