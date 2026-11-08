using System.Text.Json.Nodes;
using DoMa.App.Capture;
using DoMa.Core.Engine;
using DoMa.Core.Files;
using DoMa.Core.Messaging;
using DoMa.Core.Models;
using DoMa.Core.Queue;
using DoMa.Core.Storage;

namespace DoMa.App.Tests;

public sealed class CaptureCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DoMaTests", Guid.NewGuid().ToString("N"));
    private readonly Settings _settings = new();
    private readonly FakeDialog _dialog = new();
    private readonly List<string> _replies = [];
    private readonly List<string> _notices = [];
    private ProbeResult _probe = Ok("Win10.iso");
    private bool _connected = true;
    private Func<string, bool> _delivered = _ => true;

    public CaptureCoordinatorTests()
    {
        Directory.CreateDirectory(_dir);
        _settings.RootFolder = Path.Combine(_dir, "root");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static ProbeResult Ok(string? fileName) =>
        new(true, null, 1000, true, null, null, fileName, null, ProbeFailure.None, 206);

    private DownloadManager Manager() =>
        new(new IdleEngine(), (_, _) => Task.FromResult(_probe), new DownloadStore(_dir), () => _settings, Path.Combine(_dir, "states"));

    private CaptureCoordinator Coordinator(DownloadManager manager) =>
        new((_, _) => Task.FromResult(_probe), _dialog, manager, () => new CategoryResolver(_settings.Extensions, _settings.RootFolder), _notices.Add);

    private Task<bool> Reply(JsonNode node)
    {
        var type = Messages.TypeOf(node)!;
        lock (_replies)
            _replies.Add(type);
        return Task.FromResult(_delivered(type));
    }

    private bool Connected() => _connected;

    private static JsonNode Capture(string id, string url = "https://h/x", string? filename = null) =>
        new JsonObject { ["type"] = MessageTypes.Capture, ["id"] = id, ["url"] = url, ["filename"] = filename };

    [Fact]
    public async Task probe_failure_replies_tra_lai_without_dialog()
    {
        _probe = new ProbeResult(false, null, null, false, null, null, null, null, ProbeFailure.HtmlInsteadOfFile, 200);
        var manager = Manager();

        await Coordinator(manager).HandleAsync(Capture("1"), Reply, Connected);

        Assert.Equal([MessageTypes.GiveBack], _replies);
        Assert.Equal(0, _dialog.Calls);
        Assert.Empty(manager.Items);
    }

    [Fact]
    public async Task download_now_replies_dang_hoi_then_nhan_and_adds_started()
    {
        var manager = Manager();
        _dialog.Next = (_, path) => new DialogResult(DialogChoice.DownloadNow, path);

        await Coordinator(manager).HandleAsync(Capture("1"), Reply, Connected);

        Assert.Equal([MessageTypes.Asking, MessageTypes.Accepted], _replies);
        var item = Assert.Single(manager.Items);
        Assert.NotEqual(DownloadStatus.Later, item.Status);
    }

    [Fact]
    public async Task later_adds_later_status()
    {
        var manager = Manager();
        _dialog.Next = (_, path) => new DialogResult(DialogChoice.Later, path);

        await Coordinator(manager).HandleAsync(Capture("1"), Reply, Connected);

        Assert.Equal([MessageTypes.Asking, MessageTypes.Accepted], _replies);
        Assert.Equal(DownloadStatus.Later, Assert.Single(manager.Items).Status);
    }

    [Fact]
    public async Task cancel_replies_huy_and_adds_nothing()
    {
        var manager = Manager();
        _dialog.Next = (_, path) => new DialogResult(DialogChoice.Cancel, path);

        await Coordinator(manager).HandleAsync(Capture("1"), Reply, Connected);

        Assert.Equal([MessageTypes.Asking, MessageTypes.Cancelled], _replies);
        Assert.Empty(manager.Items);
    }

    [Fact]
    public async Task proposed_path_uses_category_folder()
    {
        var manager = Manager();

        await Coordinator(manager).HandleAsync(Capture("1"), Reply, Connected);

        Assert.Equal(Path.Combine(_settings.RootFolder, "PhanMem", "Win10.iso"), _dialog.Proposed.Single());
    }

    [Fact]
    public async Task file_name_falls_back_to_suggestion_then_url_segment()
    {
        var manager = Manager();
        var coordinator = Coordinator(manager);

        _probe = Ok(null);
        await coordinator.HandleAsync(Capture("1", "https://h/a/b.zip", "hint.pdf"), Reply, Connected);
        await coordinator.HandleAsync(Capture("2", "https://h/a/b%20c.zip"), Reply, Connected);

        Assert.Equal(Path.Combine(_settings.RootFolder, "TaiLieu", "hint.pdf"), _dialog.Proposed[0]);
        Assert.Equal(Path.Combine(_settings.RootFolder, "Nen", "b c.zip"), _dialog.Proposed[1]);
    }

    [Fact]
    public async Task malformed_message_with_id_replies_tra_lai()
    {
        var manager = Manager();
        var msg = new JsonObject { ["type"] = MessageTypes.Capture, ["id"] = "7", ["url"] = 5 };

        await Coordinator(manager).HandleAsync(msg, Reply, Connected);

        Assert.Equal([MessageTypes.GiveBack], _replies);
        Assert.Equal(0, _dialog.Calls);
    }

    [Fact]
    public async Task malformed_message_without_readable_id_is_ignored()
    {
        var manager = Manager();
        var msg = new JsonObject { ["type"] = MessageTypes.Capture, ["id"] = 7, ["url"] = 5 };

        await Coordinator(manager).HandleAsync(msg, Reply, Connected);

        Assert.Empty(_replies);
        Assert.Equal(0, _dialog.Calls);
    }

    [Fact]
    public async Task concurrent_captures_of_same_file_get_distinct_targets()
    {
        var manager = Manager();
        var coordinator = Coordinator(manager);
        var gate = new TaskCompletionSource();
        _dialog.Gate = gate.Task;
        _dialog.Next = (_, path) => new DialogResult(DialogChoice.Later, path);

        var first = coordinator.HandleAsync(Capture("1"), Reply, Connected);
        var second = coordinator.HandleAsync(Capture("2"), Reply, Connected);
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, _dialog.Proposed.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(2, manager.Items.Select(i => i.TargetPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task target_of_existing_item_is_not_proposed_again()
    {
        var manager = Manager();
        var coordinator = Coordinator(manager);
        _dialog.Next = (_, path) => new DialogResult(DialogChoice.Later, path);

        await coordinator.HandleAsync(Capture("1"), Reply, Connected);
        await coordinator.HandleAsync(Capture("2"), Reply, Connected);

        Assert.EndsWith("Win10 (1).iso", _dialog.Proposed[1]);
    }

    [Fact]
    public async Task existing_file_on_disk_is_not_proposed()
    {
        var manager = Manager();
        var folder = Path.Combine(_settings.RootFolder, "PhanMem");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Win10.iso"), "x");

        await Coordinator(manager).HandleAsync(Capture("1"), Reply, Connected);

        Assert.EndsWith("Win10 (1).iso", _dialog.Proposed.Single());
    }

    [Fact]
    public async Task throwing_dialog_replies_tra_lai_and_releases_reservation()
    {
        var manager = Manager();
        var coordinator = Coordinator(manager);
        _dialog.Next = (_, _) => throw new InvalidOperationException();

        await coordinator.HandleAsync(Capture("1"), Reply, Connected);
        await coordinator.HandleAsync(Capture("2"), Reply, Connected);

        Assert.Equal([MessageTypes.Asking, MessageTypes.GiveBack, MessageTypes.Asking, MessageTypes.GiveBack], _replies);
        Assert.Equal(_dialog.Proposed[0], _dialog.Proposed[1]);
    }

    [Fact]
    public async Task throwing_add_replies_tra_lai()
    {
        var manager = Manager();
        _dialog.Next = (_, _) => new DialogResult(DialogChoice.DownloadNow, null!);

        await Coordinator(manager).HandleAsync(Capture("1"), Reply, Connected);

        Assert.Equal([MessageTypes.Asking, MessageTypes.GiveBack], _replies);
        Assert.Empty(manager.Items);
    }

    [Fact]
    public async Task failing_dang_hoi_reply_does_not_throw_and_releases_reservation()
    {
        var manager = Manager();
        var coordinator = Coordinator(manager);

        await coordinator.HandleAsync(Capture("1"), _ => throw new IOException(), Connected);
        await coordinator.HandleAsync(Capture("2"), Reply, Connected);

        Assert.Equal(1, _dialog.Calls);
        Assert.Equal(Path.Combine(_settings.RootFolder, "PhanMem", "Win10.iso"), _dialog.Proposed.Single());
    }

    [Fact]
    public async Task url_without_file_name_gets_fallback_name()
    {
        _probe = Ok(null);
        var manager = Manager();

        await Coordinator(manager).HandleAsync(Capture("1", "https://h/"), Reply, Connected);

        Assert.Equal(Path.Combine(_settings.RootFolder, "Khac", "download"), _dialog.Proposed.Single());
    }

    [Theory]
    [InlineData(DialogChoice.DownloadNow)]
    [InlineData(DialogChoice.Later)]
    public async Task lost_connection_during_dialog_adds_nothing_and_notifies(DialogChoice choice)
    {
        var manager = Manager();
        _dialog.Next = (_, path) =>
        {
            _connected = false;
            return new DialogResult(choice, path);
        };

        await Coordinator(manager).HandleAsync(Capture("1"), Reply, Connected);

        Assert.Equal([MessageTypes.Asking], _replies);
        Assert.Empty(manager.Items);
        Assert.Equal(["Trình duyệt đã tự tải lại tệp này vì mất kết nối với DoMa."], _notices);
    }

    [Fact]
    public async Task undelivered_nhan_removes_added_item_and_notifies()
    {
        var manager = Manager();
        _dialog.Next = (_, path) => new DialogResult(DialogChoice.DownloadNow, path);
        _delivered = type => type != MessageTypes.Accepted;

        await Coordinator(manager).HandleAsync(Capture("1"), Reply, Connected);

        Assert.Equal([MessageTypes.Asking, MessageTypes.Accepted], _replies);
        Assert.Empty(manager.Items);
        Assert.Equal(["Trình duyệt đã tự tải lại tệp này vì mất kết nối với DoMa."], _notices);
    }

    [Fact]
    public async Task throwing_nhan_reply_removes_added_item()
    {
        var manager = Manager();
        _dialog.Next = (_, path) => new DialogResult(DialogChoice.Later, path);

        await Coordinator(manager).HandleAsync(
            Capture("1"),
            node => Messages.TypeOf(node) == MessageTypes.Accepted ? throw new IOException() : Reply(node),
            Connected);

        Assert.Empty(manager.Items);
        Assert.Single(_notices);
    }

    private sealed class FakeDialog : IDownloadDialog
    {
        private readonly List<string> _proposed = [];

        public Func<DownloadRequest, string, DialogResult> Next { get; set; } = (_, path) => new DialogResult(DialogChoice.Cancel, path);
        public Task? Gate { get; set; }
        public int Calls { get; private set; }
        public IReadOnlyList<string> Proposed
        {
            get
            {
                lock (_proposed)
                    return _proposed.ToArray();
            }
        }

        public async Task<DialogResult> ShowAsync(DownloadRequest req, ProbeResult probe, string proposedPath, Category category, DownloadItem? existing = null)
        {
            lock (_proposed)
            {
                Calls++;
                _proposed.Add(proposedPath);
            }
            if (Gate is not null)
                await Gate;
            return Next(req, proposedPath);
        }
    }

    private sealed class IdleEngine : IDownloadEngine
    {
        public async Task<EngineResult> RunAsync(DownloadJob job, IProgress<EngineProgress> progress, CancellationToken pauseToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, pauseToken);
            }
            catch (OperationCanceledException)
            {
            }
            return new EngineResult(EngineOutcome.Paused, null, null);
        }
    }
}
