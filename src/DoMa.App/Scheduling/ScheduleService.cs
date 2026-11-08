using DoMa.Core.Models;
using DoMa.Core.Queue;
using DoMa.Core.Scheduling;
using DoMa.Core.Storage;

namespace DoMa.App.Scheduling;

public enum AfterDone { Nothing, Sleep, Shutdown }

/// <summary>
/// Tải theo lịch (tự bắt đầu hàng "Chờ tải sau" vào giờ hẹn, tự đưa về hàng chờ khi hết giờ)
/// và hành động sau khi tải xong tất cả (ngủ / tắt máy, có đếm ngược để huỷ).
/// </summary>
internal sealed class ScheduleService : IDisposable
{
    private readonly DownloadManager _manager;
    private readonly Func<Settings> _settings;
    private readonly Action<string> _notify;
    private readonly Func<AfterDone, bool> _confirmPower;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15000 };
    private readonly HashSet<Guid> _scheduled = [];
    private DateTime _lastCheck = DateTime.Now;
    private bool _sawActive;
    private bool _prompting;

    public ScheduleService(DownloadManager manager, Func<Settings> settings, Action<string> notify, Func<AfterDone, bool> confirmPower)
    {
        _manager = manager;
        _settings = settings;
        _notify = notify;
        _confirmPower = confirmPower;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public event Action? Changed;

    public AfterDone AfterDone { get; private set; }

    public void SetAfterDone(AfterDone value)
    {
        AfterDone = value;
        _sawActive = HasActive();
        Changed?.Invoke();
    }

    /// <summary>Bắt đầu ngay mọi tệp trong hàng "Chờ tải sau".</summary>
    public int StartLaterNow()
    {
        var ids = _manager.Items.Where(i => i.Status == DownloadStatus.Later).Select(i => i.Id).ToList();
        if (ids.Count > 0)
            _manager.Resume(ids);
        return ids.Count;
    }

    /// <summary>Mô tả ngắn cho thanh trạng thái.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        var s = _settings();
        if (s.SpeedLimitKBps > 0)
            parts.Add(L.F("Giới hạn {0}", UI.DownloadView.FormatSpeed(s.SpeedLimitKBps * 1024.0)));
        if (s.ScheduleEnabled && ScheduleClock.TryParse(s.ScheduleStart, out var start))
            parts.Add(ScheduleClock.TryParse(s.ScheduleStop, out var stop) ? L.F("Lịch {0:HH\\:mm}–{1:HH\\:mm}", start, stop) : L.F("Lịch từ {0:HH\\:mm}", start));
        if (AfterDone != AfterDone.Nothing)
            parts.Add(AfterDone == AfterDone.Sleep ? L.T("Xong sẽ cho máy ngủ") : L.T("Xong sẽ tắt máy"));
        return string.Join(" · ", parts);
    }

    private bool HasActive() => _manager.Items.Any(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Queued);

    private void Tick()
    {
        var now = DateTime.Now;
        var previous = _lastCheck;
        _lastCheck = now;
        RunSchedule(previous, now);
        CheckAfterDone();
    }

    private void RunSchedule(DateTime previous, DateTime now)
    {
        var s = _settings();
        if (!s.ScheduleEnabled || !ScheduleClock.TryParse(s.ScheduleStart, out var start))
            return;
        TimeOnly? stop = ScheduleClock.TryParse(s.ScheduleStop, out var t) ? t : null;
        switch (ScheduleClock.Evaluate(start, stop, previous, now))
        {
            case ScheduleAction.Start:
                var ids = _manager.Items.Where(i => i.Status == DownloadStatus.Later).Select(i => i.Id).ToList();
                if (ids.Count == 0)
                    return;
                _scheduled.Clear();
                _scheduled.UnionWith(ids);
                _manager.Resume(ids);
                _notify(L.F("Đã bắt đầu tải theo lịch: {0} tệp.", ids.Count));
                break;
            case ScheduleAction.Stop:
                var stopIds = _manager.Items
                    .Where(i => _scheduled.Contains(i.Id) && i.Status is DownloadStatus.Downloading or DownloadStatus.Queued)
                    .Select(i => i.Id).ToList();
                _scheduled.Clear();
                if (stopIds.Count == 0)
                    return;
                _manager.Defer(stopIds);
                _notify(L.F("Hết giờ tải theo lịch: {0} tệp được đưa về hàng chờ.", stopIds.Count));
                break;
        }
    }

    private void CheckAfterDone()
    {
        if (AfterDone == AfterDone.Nothing || _prompting)
            return;
        if (HasActive())
        {
            _sawActive = true;
            return;
        }
        if (!_sawActive)
            return;
        var action = AfterDone;
        _prompting = true;
        try
        {
            SetAfterDone(AfterDone.Nothing);
            if (_confirmPower(action))
                Execute(action);
        }
        finally
        {
            _prompting = false;
        }
    }

    private static void Execute(AfterDone action)
    {
        try
        {
            if (action == AfterDone.Sleep)
                Application.SetSuspendState(PowerState.Suspend, false, false);
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
