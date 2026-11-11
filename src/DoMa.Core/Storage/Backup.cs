using System.IO.Compression;
using System.Text.Json;

namespace DoMa.Core.Storage;

/// <summary>
/// Sao lưu và khôi phục dữ liệu của DoMa (cài đặt, danh sách tải, kênh đang theo dõi) bằng một tệp .zip.
/// Khôi phục không ghi đè ngay lúc DoMa đang chạy: tệp được để sẵn trong thư mục "restore" rồi áp dụng ở lần mở kế tiếp.
/// </summary>
public static class Backup
{
    public static readonly IReadOnlyList<string> Files = ["settings.json", "downloads.json", "channels.json"];

    private const long MaxFileBytes = 64L << 20;

    private static string PendingDir(string dataDir) => Path.Combine(dataDir, "restore");

    /// <summary>Ghi các tệp dữ liệu hiện có vào <paramref name="zipPath"/>; trả về số tệp đã ghi.</summary>
    public static int Create(string dataDir, string zipPath)
    {
        var temp = zipPath + ".tmp";
        var count = 0;
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var name in Files)
                {
                    var path = Path.Combine(dataDir, name);
                    if (!File.Exists(path))
                        continue;
                    zip.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
                    count++;
                }
            }
            File.Move(temp, zipPath, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
        return count;
    }

    /// <summary>
    /// Kiểm tra tệp sao lưu rồi để sẵn dữ liệu cho lần mở DoMa kế tiếp. Chỉ nhận đúng các tệp dữ liệu của DoMa,
    /// mỗi tệp phải là JSON hợp lệ. Báo <see cref="InvalidDataException"/> nếu đây không phải tệp sao lưu của DoMa.
    /// </summary>
    public static int Stage(string zipPath, string dataDir)
    {
        var pending = PendingDir(dataDir);
        var staging = pending + ".new";
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        var count = 0;
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var name in Files)
                {
                    var entry = zip.Entries.FirstOrDefault(e => e.FullName == name);
                    if (entry is null)
                        continue;
                    if (entry.Length > MaxFileBytes)
                        throw new InvalidDataException(name);
                    var target = Path.Combine(staging, name);
                    entry.ExtractToFile(target, true);
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllBytes(target));
                    }
                    catch (JsonException e)
                    {
                        throw new InvalidDataException(name, e);
                    }
                    count++;
                }
            }
            if (count == 0)
                throw new InvalidDataException(L.T("Tệp này không phải bản sao lưu của DoMa."));
            if (Directory.Exists(pending))
                Directory.Delete(pending, true);
            Directory.Move(staging, pending);
            return count;
        }
        catch
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
            throw;
        }
    }

    /// <summary>Gọi lúc mở DoMa, trước khi đọc dữ liệu: đưa dữ liệu chờ khôi phục vào chỗ. Trả về true nếu có khôi phục.</summary>
    public static bool ApplyPending(string dataDir)
    {
        var pending = PendingDir(dataDir);
        if (!Directory.Exists(pending))
            return false;
        var applied = false;
        foreach (var name in Files)
        {
            var source = Path.Combine(pending, name);
            if (!File.Exists(source))
                continue;
            File.Move(source, Path.Combine(dataDir, name), true);
            applied = true;
        }
        Directory.Delete(pending, true);
        return applied;
    }
}
