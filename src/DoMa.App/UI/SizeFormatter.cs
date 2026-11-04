using System.Globalization;

namespace DoMa.App.UI;

public static class SizeFormatter
{
    public const string Unknown = "Không rõ";

    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long? bytes)
    {
        if (bytes is null or < 0)
            return Unknown;
        if (bytes < 1024)
            return $"{bytes} B";
        var value = (double)bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString("0.0", Vi)} {Units[unit]}";
    }
}
