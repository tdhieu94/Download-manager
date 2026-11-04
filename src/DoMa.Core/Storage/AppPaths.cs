namespace DoMa.Core.Storage;

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoMa");
    public static string StatesDir { get; } = Path.Combine(DataDir, "states");
}
