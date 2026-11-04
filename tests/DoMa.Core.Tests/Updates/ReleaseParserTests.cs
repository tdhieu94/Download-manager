using DoMa.Core.Updates;

namespace DoMa.Core.Tests.Updates;

public class ReleaseParserTests
{
    const string Hash = "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899";

    static string Json(string tag, bool prerelease = false, string? digest = null, bool shaFile = false) => $$"""
    {
      "tag_name": "{{tag}}",
      "draft": false,
      "prerelease": {{(prerelease ? "true" : "false")}},
      "html_url": "https://github.com/x/y/releases/tag/{{tag}}",
      "assets": [
        { "name": "notes.txt", "browser_download_url": "https://github.com/x/y/notes.txt" },
        { "name": "DoMa-Setup-1.2.0.exe", "browser_download_url": "https://github.com/x/y/DoMa-Setup-1.2.0.exe"{{(digest is null ? "" : $", \"digest\": \"{digest}\"")}} }
        {{(shaFile ? ", { \"name\": \"DoMa-Setup-1.2.0.exe.sha256\", \"browser_download_url\": \"https://github.com/x/y/DoMa-Setup-1.2.0.exe.sha256\" }" : "")}}
      ]
    }
    """;

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("V2", "2.0.0")]
    [InlineData("v1.4.0-beta.1", "1.4.0")]
    public void parses_versions(string text, string expected)
    {
        Assert.Equal(Version.Parse(expected), ReleaseParser.ParseVersion(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void rejects_bad_versions(string? text)
    {
        Assert.Null(ReleaseParser.ParseVersion(text));
    }

    [Fact]
    public void reads_setup_asset_and_digest()
    {
        var release = ReleaseParser.Parse(Json("v1.2.0", digest: "sha256:" + Hash.ToUpperInvariant()))!;
        Assert.Equal(new Version(1, 2, 0), release.Version);
        Assert.Equal("DoMa-Setup-1.2.0.exe", release.SetupName);
        Assert.Equal(Hash, release.Sha256);
        Assert.Null(release.Sha256Url);
    }

    [Fact]
    public void falls_back_to_sha_file()
    {
        var release = ReleaseParser.Parse(Json("v1.2.0", shaFile: true))!;
        Assert.Null(release.Sha256);
        Assert.EndsWith(".exe.sha256", release.Sha256Url);
    }

    [Fact]
    public void ignores_prerelease()
    {
        Assert.Null(ReleaseParser.Parse(Json("v1.2.0", prerelease: true)));
    }

    [Fact]
    public void compares_with_assembly_version()
    {
        var release = ReleaseParser.Parse(Json("v1.2.0", digest: "sha256:" + Hash))!;
        Assert.True(ReleaseParser.IsNewer(release, new Version(1, 1, 9, 0)));
        Assert.False(ReleaseParser.IsNewer(release, new Version(1, 2, 0, 0)));
        Assert.False(ReleaseParser.IsNewer(release, new Version(1, 3, 0)));
    }

    [Theory]
    [InlineData(Hash, Hash)]
    [InlineData(Hash + "  DoMa-Setup-1.2.0.exe\n", Hash)]
    [InlineData("not-a-hash", null)]
    public void reads_sha_file(string text, string? expected)
    {
        Assert.Equal(expected, ReleaseParser.ParseShaFile(text));
    }
}
