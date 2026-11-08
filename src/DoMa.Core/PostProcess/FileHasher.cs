using System.Security.Cryptography;

namespace DoMa.Core.PostProcess;

public sealed record FileHashes(string Md5, string Sha1, string Sha256);

/// <summary>Tính MD5, SHA-1 và SHA-256 của một tệp trong một lần đọc.</summary>
public static class FileHasher
{
    public static async Task<FileHashes> ComputeAsync(string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, true);
        var total = stream.Length;
        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            var span = buffer.AsSpan(0, read);
            md5.AppendData(span);
            sha1.AppendData(span);
            sha256.AppendData(span);
            done += read;
            if (total > 0)
                progress?.Report((double)done / total);
        }
        return new FileHashes(
            Convert.ToHexStringLower(md5.GetHashAndReset()),
            Convert.ToHexStringLower(sha1.GetHashAndReset()),
            Convert.ToHexStringLower(sha256.GetHashAndReset()));
    }

    /// <summary>
    /// So mã băm người dùng dán vào (bỏ khoảng trắng, không phân biệt hoa thường; chấp nhận dạng "HASH  tên-file").
    /// Trả về tên thuật toán khớp, hoặc null nếu không khớp.
    /// </summary>
    /// <summary>Mã băm (MD5/SHA-1/SHA-256, dạng hex) đầu tiên trong đoạn văn bản, vd. nội dung vừa copy; null nếu không có.</summary>
    public static string? FindHash(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000)
            return null;
        foreach (var token in text.Split([' ', '\t', '\r', '\n', '*', ':', '=', ',', ';', '(', ')', '"', '\''], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length is 32 or 40 or 64 && token.All(char.IsAsciiHexDigit))
                return token.ToLowerInvariant();
        }
        return null;
    }

    public static string? Match(string expected, FileHashes hashes)
    {
        var token = expected.Trim().Split([' ', '\t', '\r', '\n', '*'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        if (string.IsNullOrEmpty(token))
            return null;
        if (token == hashes.Sha256)
            return "SHA-256";
        if (token == hashes.Sha1)
            return "SHA-1";
        return token == hashes.Md5 ? "MD5" : null;
    }
}
