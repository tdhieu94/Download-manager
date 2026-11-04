using System.Diagnostics;

namespace DoMa.Core.PostProcess;

public enum ScanResult { Clean, ThreatFound, Unavailable, Failed }

/// <summary>Đánh dấu tệp tải từ Internet và quét bằng Windows Defender.</summary>
public static class SecurityTools
{
    /// <summary>
    /// Ghi "Mark of the Web" (luồng Zone.Identifier) như trình duyệt, để Windows SmartScreen / Office
    /// cảnh báo khi mở tệp tải từ Internet. Chỉ ghi tên miền, không ghi đường dẫn hay token.
    /// </summary>
    public static void MarkFromInternet(string path, string url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}/" : null;
        var text = "[ZoneTransfer]\r\nZoneId=3\r\n" + (host is null ? "" : $"HostUrl={host}\r\n");
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Ổ FAT32 / ổ mạng không hỗ trợ luồng dữ liệu phụ: bỏ qua.
        }
    }

    public static string? DefenderPath()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Quét một tệp bằng Windows Defender (MpCmdRun). Mối nguy được Defender tự xử lý theo cấu hình của máy.</summary>
    public static async Task<ScanResult> ScanAsync(string path, CancellationToken ct)
    {
        if (DefenderPath() is not { } exe)
            return ScanResult.Unavailable;
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-Scan", "-ScanType", "3", "-File", path })
            info.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return ScanResult.Failed;
            _ = process.StandardOutput.ReadToEndAsync(ct);
            _ = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return process.ExitCode switch
            {
                0 => ScanResult.Clean,
                2 => ScanResult.ThreatFound,
                _ => ScanResult.Failed,
            };
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ScanResult.Failed;
        }
    }
}
