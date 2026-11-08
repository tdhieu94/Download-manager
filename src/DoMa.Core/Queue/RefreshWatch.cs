using System.Text.RegularExpressions;
using DoMa.Core.Models;

namespace DoMa.Core.Queue;

/// <summary>
/// Theo dõi các tệp đang chờ link mới (link cũ hết hạn). Khi người dùng mở lại trang nguồn và bấm tải,
/// link bắt được sẽ được nhận cho tệp cũ nếu trùng tên tệp hoặc trùng dung lượng.
/// </summary>
public sealed partial class RefreshWatch(TimeProvider? time = null)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private const long MinSizeMatch = 1L << 20;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<Guid, DateTimeOffset> _armed = [];
    private readonly object _gate = new();

    public void Arm(Guid id)
    {
        lock (_gate)
            _armed[id] = _time.GetUtcNow();
    }

    public void Disarm(Guid id)
    {
        lock (_gate)
            _armed.Remove(id);
    }

    public bool IsArmed(Guid id)
    {
        lock (_gate)
            return _armed.TryGetValue(id, out var at) && _time.GetUtcNow() - at <= Window;
    }

    /// <summary>Tìm tệp đang chờ link mới khớp với link vừa bắt; ưu tiên trùng tên, sau đó trùng dung lượng.</summary>
    public DownloadItem? Match(IEnumerable<DownloadItem> items, string fileName, long? size)
    {
        List<DownloadItem> waiting;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var id in _armed.Where(a => now - a.Value > Window).Select(a => a.Key).ToList())
                _armed.Remove(id);
            if (_armed.Count == 0)
                return null;
            waiting = items.Where(i => _armed.ContainsKey(i.Id) && i.Status != DownloadStatus.Completed).ToList();
        }
        var name = Normalize(fileName);
        return waiting.FirstOrDefault(i => name.Length > 0 && Normalize(Path.GetFileName(i.TargetPath)) == name)
            ?? waiting.FirstOrDefault(i => size is > MinSizeMatch && i.TotalBytes == size);
    }

    /// <summary>Bỏ phần " (2)" mà DoMa thêm khi trùng tên, so không phân biệt hoa thường.</summary>
    public static string Normalize(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return (CopySuffix().Replace(stem, "") + ext).Trim().ToLowerInvariant();
    }

    [GeneratedRegex(@"\s\(\d+\)$")]
    private static partial Regex CopySuffix();
}
