using DoMa.Core.PostProcess;

namespace DoMa.Core.Tests.PostProcess;

public sealed class DownloadRiskTests
{
    [Theory]
    [InlineData("https://example.com/report.pdf", "report.pdf", "application/pdf")]
    [InlineData("https://example.com/photos.zip", "photos.zip", null)]
    [InlineData("http://example.com/movie.mp4", "movie.mp4", "video/mp4")]
    public void ordinary_files_are_safe(string url, string name, string? type) =>
        Assert.Equal(RiskLevel.None, DownloadRisk.Assess(url, null, name, type).Level);

    [Fact]
    public void executable_over_https_is_caution()
    {
        var r = DownloadRisk.Assess("https://example.com/setup.exe", null, "setup.exe", "application/octet-stream");
        Assert.Equal(RiskLevel.Caution, r.Level);
        Assert.Contains(r.Reasons, s => s.Contains("example.com"));
    }

    [Theory]
    [InlineData("hoadon.pdf.exe")]
    [InlineData("anh.jpg.scr")]
    [InlineData("hoadon‮fdp.exe")]
    [InlineData("tai lieu.docx          .exe")]
    public void disguised_names_are_high(string name) =>
        Assert.Equal(RiskLevel.High, DownloadRisk.Assess("https://example.com/x", null, name, null).Level);

    [Fact]
    public void executable_content_with_document_name_is_high() =>
        Assert.Equal(RiskLevel.High, DownloadRisk.Assess("https://example.com/a", null, "invoice.pdf", "application/x-msdownload; charset=binary").Level);

    [Fact]
    public void executable_over_http_or_ip_lists_each_reason()
    {
        var r = DownloadRisk.Assess("http://203.0.113.5/tool.msi", null, "tool.msi", null);
        Assert.Equal(RiskLevel.Caution, r.Level);
        Assert.Equal(3, r.Reasons.Count);
    }

    [Fact]
    public void uses_final_url_after_redirect()
    {
        var r = DownloadRisk.Assess("https://example.com/get", "http://198.51.100.7/app.exe", "app.exe", null);
        Assert.Contains(r.Reasons, s => s.Contains("198.51.100.7"));
    }
}
