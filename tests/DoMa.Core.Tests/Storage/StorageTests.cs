using DoMa.Core.Models;
using DoMa.Core.Storage;

namespace DoMa.Core.Tests.Storage;

public class StorageTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "doma-storage-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    static DownloadItem Item(DownloadStatus status, string? cookies) => new()
    {
        Id = Guid.NewGuid(),
        Request = new DownloadRequest("https://h/p/f.iso?token=x", "https://h/", "f.iso", "application/octet-stream", 10, cookies, "ua"),
        TargetPath = @"C:\d\f.iso",
        Status = status,
        AddedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void settings_roundtrip()
    {
        var store = new SettingsStore(dir);
        var s = new Settings { RootFolder = @"D:\x", MaxConcurrent = 5, ConnectionsPerFile = 8, RunAtStartup = true, NotifyOnComplete = false };
        s.Extensions[Category.Other] = ["abc"];
        store.Save(s);
        var loaded = store.Load();
        Assert.Equal(@"D:\x", loaded.RootFolder);
        Assert.Equal(5, loaded.MaxConcurrent);
        Assert.Equal(8, loaded.ConnectionsPerFile);
        Assert.True(loaded.RunAtStartup);
        Assert.False(loaded.NotifyOnComplete);
        Assert.Equal(["abc"], loaded.Extensions[Category.Other]);
        Assert.Contains("pdf", loaded.Extensions[Category.Document]);
    }

    [Fact]
    public void old_settings_get_media_category()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Extensions\":{\"Document\":[\"pdf\"]}}");

        var loaded = new SettingsStore(dir).Load();

        Assert.Equal(["pdf"], loaded.Extensions[Category.Document]);
        Assert.Contains("mp4", loaded.Extensions[Category.Media]);
    }

    [Fact]
    public void connections_clamped()
    {
        Assert.Equal(32, new Settings { ConnectionsPerFile = 40 }.ConnectionsPerFile);
        Assert.Equal(1, new Settings { ConnectionsPerFile = 0 }.ConnectionsPerFile);
    }

    [Fact]
    public void cookies_not_plaintext_on_disk()
    {
        var store = new DownloadStore(dir);
        store.Save([Item(DownloadStatus.Paused, "sid=SECRET")]);
        Assert.DoesNotContain("SECRET", File.ReadAllText(Path.Combine(dir, "downloads.json")));
        Assert.Equal("sid=SECRET", store.Load().Single().Request.Cookies);
    }

    [Fact]
    public void downloading_loads_as_paused()
    {
        var store = new DownloadStore(dir);
        store.Save([Item(DownloadStatus.Downloading, null), Item(DownloadStatus.Completed, null)]);
        var loaded = store.Load();
        Assert.Equal(DownloadStatus.Paused, loaded[0].Status);
        Assert.Equal(DownloadStatus.Completed, loaded[1].Status);
    }

    [Fact]
    public void redact_strips_query()
    {
        Assert.Equal("https://h/p/f.iso", ErrorLog.RedactUrl("https://h/p/f.iso?token=x"));
    }

    [Fact]
    public void log_has_no_cookie()
    {
        var item = Item(DownloadStatus.Failed, "sid=SECRET");
        new ErrorLog(dir).Write("download", new InvalidOperationException("boom"), item.Request.Url);
        var text = File.ReadAllText(Path.Combine(dir, "errors.log"));
        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("token=x", text);
        Assert.Contains("https://h/p/f.iso", text);
    }

    [Fact]
    public void log_redacts_urls_in_message()
    {
        new ErrorLog(dir).Write("download", new InvalidOperationException("failed https://h/p?token=x done"), null);
        var text = File.ReadAllText(Path.Combine(dir, "errors.log"));
        Assert.DoesNotContain("token=x", text);
        Assert.Contains("https://h/p", text);
    }

    [Fact]
    public void redact_strips_userinfo()
    {
        Assert.Equal("https://h:8443/p", ErrorLog.RedactUrl("https://user:pass@h:8443/p?q=1#f"));
        Assert.Equal("https://h/p", ErrorLog.RedactUrl("https://user:pass@h/p"));
    }

    [Fact]
    public void undecryptable_cookie_loads_as_null()
    {
        var store = new DownloadStore(dir);
        store.Save([Item(DownloadStatus.Paused, "sid=SECRET"), Item(DownloadStatus.Paused, null)]);
        var path = Path.Combine(dir, "downloads.json");
        var text = File.ReadAllText(path);
        var blob = System.Text.RegularExpressions.Regex.Match(text, "\"ProtectedCookies\": \"([^\"]+)\"").Groups[1].Value;
        File.WriteAllText(path, text.Replace(blob, "AAAA"));
        var loaded = store.Load();
        Assert.Equal(2, loaded.Count);
        Assert.Null(loaded[0].Request.Cookies);
    }

    [Fact]
    public void corrupt_downloads_file_is_renamed_and_empty()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "downloads.json"), "{not json");
        Assert.Empty(new DownloadStore(dir).Load());
        Assert.True(File.Exists(Path.Combine(dir, "downloads.json.bad")));
        Assert.False(File.Exists(Path.Combine(dir, "downloads.json")));
    }

    [Fact]
    public void parallel_saves_do_not_collide()
    {
        var store = new DownloadStore(dir);
        var items = new[] { Item(DownloadStatus.Paused, "sid=1") };
        Parallel.For(0, 200, _ => store.Save(items));
        Assert.Single(store.Load());
    }

    [Fact]
    public void corrupt_settings_fall_back_to_defaults()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "garbage");
        var s = new SettingsStore(dir).Load();
        Assert.Equal(16, s.ConnectionsPerFile);
        Assert.True(File.Exists(Path.Combine(dir, "settings.json.bad")));
    }

    [Fact]
    public void null_or_partial_extensions_filled_from_defaults()
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{\"Extensions\": null}");
        Assert.Contains("pdf", new SettingsStore(dir).Load().Extensions[Category.Document]);
        File.WriteAllText(path, "{\"Extensions\": {\"Archive\": [\"zz\"]}}");
        var s = new SettingsStore(dir).Load();
        Assert.Equal(["zz"], s.Extensions[Category.Archive]);
        Assert.Contains("pdf", s.Extensions[Category.Document]);
    }
}
