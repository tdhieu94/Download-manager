using System.IO.Compression;
using DoMa.Core.Storage;

namespace DoMa.Core.Tests.Storage;

public sealed class BackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "doma-backup-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_dir, "data");
    private string Zip => Path.Combine(_dir, "b.zip");

    public BackupTests() => Directory.CreateDirectory(Data);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void backup_then_restore_replaces_data_on_next_start()
    {
        File.WriteAllText(Path.Combine(Data, "settings.json"), "{\"MaxConcurrent\":7}");
        File.WriteAllText(Path.Combine(Data, "downloads.json"), "[]");
        File.WriteAllText(Path.Combine(Data, "errors.log"), "private");
        Assert.Equal(2, Backup.Create(Data, Zip));
        using (var zip = ZipFile.OpenRead(Zip))
            Assert.Equal(["settings.json", "downloads.json"], zip.Entries.Select(e => e.FullName));

        File.WriteAllText(Path.Combine(Data, "settings.json"), "{\"MaxConcurrent\":1}");
        Assert.Equal(2, Backup.Stage(Zip, Data));
        // Chưa đổi gì cho tới lần mở kế tiếp.
        Assert.Equal(1, new SettingsStore(Data).Load().MaxConcurrent);

        Assert.True(Backup.ApplyPending(Data));
        Assert.Equal(7, new SettingsStore(Data).Load().MaxConcurrent);
        Assert.False(Directory.Exists(Path.Combine(Data, "restore")));
        Assert.False(Backup.ApplyPending(Data));
    }

    [Fact]
    public void rejects_zip_without_doma_data_or_with_broken_json()
    {
        using (var zip = ZipFile.Open(Zip, ZipArchiveMode.Create))
            zip.CreateEntry("../evil.txt");
        Assert.Throws<InvalidDataException>(() => Backup.Stage(Zip, Data));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));

        File.Delete(Zip);
        using (var zip = ZipFile.Open(Zip, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("settings.json").Open());
            writer.Write("not json");
        }
        Assert.Throws<InvalidDataException>(() => Backup.Stage(Zip, Data));
        Assert.False(Backup.ApplyPending(Data));
    }
}
