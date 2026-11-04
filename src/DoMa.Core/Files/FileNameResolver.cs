namespace DoMa.Core.Files;

public static class FileNameResolver
{
    private const int MaxLength = 200;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray())
            .Trim(' ')
            .TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned.All(c => c == '.'))
            return "download";

        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (Reserved.Contains(stem))
            cleaned = "_" + cleaned;

        if (cleaned.Length > MaxLength)
        {
            var extPart = Path.GetExtension(cleaned);
            if (extPart.Length >= MaxLength)
                return cleaned[..MaxLength];
            cleaned = Path.GetFileNameWithoutExtension(cleaned)[..(MaxLength - extPart.Length)] + extPart;
        }
        return cleaned;
    }

    public static string MakeUnique(string fullPath, Func<string, bool> isTaken)
    {
        if (!isTaken(fullPath))
            return fullPath;

        var dir = Path.GetDirectoryName(fullPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var ext = Path.GetExtension(fullPath);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!isTaken(candidate))
                return candidate;
        }
    }
}
