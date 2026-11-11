using System.Net;
using DoMa.Core.Engine;
using DoMa.Core.Engine.YouTube;

namespace DoMa.Core.Tests.Engine;

public class ProxyConfigTests
{
    [Theory]
    [InlineData("127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData(" http://proxy.local:3128/ ", "http://proxy.local:3128")]
    [InlineData("socks5://127.0.0.1:1080", "socks5://127.0.0.1:1080")]
    [InlineData("SOCKS4://10.0.0.2:1080", "socks4://10.0.0.2:1080")]
    public void reads_common_forms(string text, string expected)
    {
        Assert.True(ProxyConfig.IsValid(text));
        Assert.Equal(expected, ProxyConfig.Normalize(text));
    }

    [Theory]
    [InlineData("proxy.local")]
    [InlineData("http://proxy.local")]
    [InlineData("ftp://proxy.local:21")]
    [InlineData("http://proxy.local:8080/path")]
    [InlineData("a b:8080")]
    [InlineData("://")]
    public void rejects_what_cannot_be_a_proxy(string text)
    {
        Assert.False(ProxyConfig.IsValid(text));
        Assert.Equal("", ProxyConfig.Normalize(text));
    }

    [Fact]
    public void empty_means_follow_system()
    {
        Assert.True(ProxyConfig.IsValid(""));
        Assert.True(ProxyConfig.IsValid(null));
        var system = new WebProxy("http://sys:1");
        var proxy = ProxyConfig.Dynamic(() => "", system);
        Assert.Equal(new Uri("http://sys:1"), proxy.GetProxy(new Uri("https://example.com/")));
    }

    [Fact]
    public void setting_is_read_on_every_request()
    {
        var setting = "";
        var proxy = ProxyConfig.Dynamic(() => setting, new WebProxy("http://sys:1"));
        setting = "socks5://127.0.0.1:1080";
        Assert.Equal(new Uri("socks5://127.0.0.1:1080"), proxy.GetProxy(new Uri("https://example.com/")));
        Assert.False(proxy.IsBypassed(new Uri("https://example.com/")));
        Assert.True(proxy.IsBypassed(new Uri("http://localhost:5000/")));
        setting = "";
        Assert.Equal(new Uri("http://sys:1"), proxy.GetProxy(new Uri("https://example.com/")));
    }

    [Fact]
    public void login_goes_to_credentials_not_the_proxy_address()
    {
        var proxy = ProxyConfig.Dynamic(() => "http://user:p%40ss@10.0.0.1:8080", new WebProxy("http://sys:1"));
        Assert.Equal(new Uri("http://10.0.0.1:8080"), proxy.GetProxy(new Uri("https://example.com/")));
        var login = proxy.Credentials!.GetCredential(new Uri("http://10.0.0.1:8080"), "Basic")!;
        Assert.Equal(("user", "p@ss"), (login.UserName, login.Password));
    }

    [Fact]
    public void tools_get_the_proxy_they_can_use()
    {
        var setting = "socks5://127.0.0.1:1080";
        var tools = new YouTubeTools(Path.GetTempPath(), new HttpClient()) { Proxy = () => setting };

        Assert.Equal("socks5://127.0.0.1:1080", tools.ProxyFor());
        // aria2c không dùng được SOCKS: để nó nối thẳng thay vì báo lỗi.
        Assert.Null(tools.ProxyFor(httpOnly: true));

        setting = "user:pw@10.0.0.1:3128";
        Assert.Equal("http://user:pw@10.0.0.1:3128", tools.ProxyFor(httpOnly: true));

        setting = "";
        Assert.Null(tools.ProxyFor());
    }
}
