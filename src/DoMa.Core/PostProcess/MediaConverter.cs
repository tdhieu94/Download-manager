using System.Diagnostics;
using System.Text;

namespace DoMa.Core.PostProcess;

/// <summary>Đổi định dạng video/nhạc đã tải bằng ffmpeg (công cụ DoMa cài cùng yt-dlp): sang MP4 hoặc MP3.</summary>
public static class MediaConverter
{
    private static readonly string[] VideoExtensions = [".ts", ".webm", ".mkv", ".mov", ".avi", ".flv", ".wmv", ".m4v", ".mp4", ".3gp"];
    private static readonly string[] AudioExtensions = [".m4a", ".aac", ".opus", ".ogg", ".wav", ".flac", ".wma", ".mka"];

    private static string Ext(string path) => Path.GetExtension(path).ToLowerInvariant();

    public static bool CanConvertToMp4(string path) => VideoExtensions.Contains(Ext(path)) && Ext(path) != ".mp4";

    public static bool CanConvertToMp3(string path) => VideoExtensions.Contains(Ext(path)) || AudioExtensions.Contains(Ext(path));

    /// <summary>Đóng gói lại sang MP4, không nén lại (nhanh, giữ nguyên chất lượng).</summary>
    public static IReadOnlyList<string> Mp4CopyArguments(string input, string output) =>
        ["-hide_banner", "-nostdin", "-y", "-i", input, "-map", "0:v?", "-map", "0:a?", "-c", "copy", "-movflags", "+faststart", output];

    /// <summary>Nén lại sang H.264 + AAC khi định dạng gốc không đóng gói thẳng vào MP4 được.</summary>
    public static IReadOnlyList<string> Mp4EncodeArguments(string input, string output) =>
        ["-hide_banner", "-nostdin", "-y", "-i", input, "-map", "0:v?", "-map", "0:a?", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
         "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", output];

    /// <summary>Tách tiếng thành MP3 chất lượng cao nhất (VBR V0).</summary>
    public static IReadOnlyList<string> Mp3Arguments(string input, string output) =>
        ["-hide_banner", "-nostdin", "-y", "-i", input, "-vn", "-c:a", "libmp3lame", "-q:a", "0", output];

    /// <summary>Chạy ffmpeg; lỗi thì xoá tệp dở và báo <see cref="IOException"/> kèm dòng lỗi cuối của ffmpeg.</summary>
    public static async Task RunAsync(string ffmpeg, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            info.ArgumentList.Add(a);
        var output = args[^1];
        using var process = Process.Start(info) ?? throw new IOException(L.T("ffmpeg không khởi động được."));
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using (ct.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }))
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        await stdout;
        var errors = await stderr;
        if (process.ExitCode == 0 && !ct.IsCancellationRequested && File.Exists(output))
            return;
        try
        {
            File.Delete(output);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        ct.ThrowIfCancellationRequested();
        var last = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        throw new IOException(L.T("ffmpeg báo lỗi") + (last.Length > 0 ? ": " + (last.Length > 160 ? last[..160] + "…" : last) : "."));
    }
}
