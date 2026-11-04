using DoMa.Core.Files;

namespace DoMa.Core.Storage;

public class SettingsStore(string dataDir)
{
    string FilePath => Path.Combine(dataDir, "settings.json");

    public Settings Load()
    {
        var s = JsonFile.Read<Settings>(FilePath) ?? new Settings();
        s.Extensions ??= [];
        foreach (var (category, list) in CategoryResolver.Defaults)
        {
            if (!s.Extensions.TryGetValue(category, out var existing) || existing is null)
                s.Extensions[category] = list.ToArray();
        }
        return s;
    }

    public void Save(Settings s) => JsonFile.Write(FilePath, s);
}
