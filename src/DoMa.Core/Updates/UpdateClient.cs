using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace DoMa.Core.Updates;

/// <summary>Kiểm tra và tải bộ cài mới từ GitHub Releases (repo công khai, không cần token).</summary>
public sealed class UpdateClient : IDisposable
{
    public const string Repository = "tdhieu94/download-manager";

    readonly HttpClient _http;

    /// <param name="proxy">Đọc địa chỉ proxy trong cài đặt (trống = theo Windows).</param>
    public UpdateClient(Version currentVersion, Func<string?>? proxy = null)
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            Proxy = Engine.ProxyConfig.Dynamic(proxy ?? (() => null)),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DoMa", ReleaseParser.Normalize(currentVersion).ToString()));
    }

    /// <summary>Trả về bản phát hành mới nhất, hoặc null nếu chưa có bản nào hợp lệ.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return ReleaseParser.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Tải bộ cài vào thư mục tạm, kiểm tra SHA-256 rồi trả về đường dẫn file. Lỗi mạng tạm thời được tự thử lại.</summary>
    public async Task<string> DownloadSetupAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct)
    {
        var expected = release.Sha256;
        if (expected is null && release.Sha256Url is not null)
            expected = ReleaseParser.ParseShaFile(await UpdateErrors.RetryAsync(c => _http.GetStringAsync(release.Sha256Url, c), ct));
        if (expected is null)
            throw new InvalidDataException(L.T("Bản phát hành không có mã SHA-256 để kiểm tra bộ cài."));

        var dir = Path.Combine(Path.GetTempPath(), "DoMa-Update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(release.SetupName));
        var temp = target + ".part";

        await UpdateErrors.RetryAsync(c => DownloadToAsync(release.SetupUrl, temp, progress, c), ct);

        string actual;
        await using (var check = File.OpenRead(temp))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temp);
            throw new InvalidDataException(L.T("Bộ cài tải về không khớp mã SHA-256, đã huỷ cập nhật."));
        }
        File.Move(temp, target, true);
        return target;
    }

    private async Task<bool> DownloadToAsync(string url, string temp, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0)
                progress?.Report((double)done / total.Value);
        }
        return true;
    }

    public void Dispose() => _http.Dispose();
}
