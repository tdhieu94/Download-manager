using System.IO.Compression;
using DoMa.Core.Files;

namespace DoMa.Core.PostProcess;

/// <summary>Giải nén .zip vào thư mục cùng tên, cạnh tệp gốc (không ghi đè, chặn đường dẫn thoát ra ngoài).</summary>
public static class ArchiveExtractor
{
    public static bool CanExtract(string path) => string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    public static string Extract(string zipPath)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
        var target = FileNameResolver.MakeUnique(Path.Combine(parent, Path.GetFileNameWithoutExtension(zipPath)), p => Directory.Exists(p) || File.Exists(p));
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(target);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(L.F("Tệp nén chứa đường dẫn không an toàn: {0}", entry.FullName));
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: false);
            }
            return target;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
