namespace DoMa.App.UI;

public static class SizeFormatter
{
    public static string Unknown => L.T("Không rõ");

    public static string Format(long? bytes) => bytes is null or < 0 ? Unknown : Core.Files.DiskSpace.Format(bytes);
}
