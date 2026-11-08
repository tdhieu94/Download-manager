namespace DoMa.App.UI;

public static class SizeFormatter
{
    public const string Unknown = "Không rõ";

    public static string Format(long? bytes) => bytes is null or < 0 ? Unknown : Core.Files.DiskSpace.Format(bytes);
}
