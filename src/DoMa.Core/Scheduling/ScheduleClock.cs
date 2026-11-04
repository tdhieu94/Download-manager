namespace DoMa.Core.Scheduling;

public enum ScheduleAction { None, Start, Stop }

/// <summary>Xác định lúc cần bắt đầu / dừng hàng chờ theo giờ hẹn hằng ngày.</summary>
public static class ScheduleClock
{
    /// <summary>
    /// So sánh hai lần kiểm tra liên tiếp (<paramref name="previous"/> → <paramref name="now"/>):
    /// nếu khoảng đó đi qua giờ bắt đầu thì trả Start, đi qua giờ dừng thì trả Stop.
    /// Khoảng dài hơn một ngày (máy ngủ lâu) chỉ xét mốc gần nhất.
    /// </summary>
    public static ScheduleAction Evaluate(TimeOnly start, TimeOnly? stop, DateTime previous, DateTime now)
    {
        if (now <= previous)
            return ScheduleAction.None;
        var lastStart = LastOccurrence(start, now);
        var lastStop = stop is { } s ? LastOccurrence(s, now) : (DateTime?)null;
        var startCrossed = lastStart > previous;
        var stopCrossed = lastStop > previous;
        if (startCrossed && stopCrossed)
            return lastStart >= lastStop ? ScheduleAction.Start : ScheduleAction.Stop;
        if (startCrossed)
            return ScheduleAction.Start;
        return stopCrossed ? ScheduleAction.Stop : ScheduleAction.None;
    }

    /// <summary>Thời điểm gần nhất (≤ now) mà đồng hồ chỉ <paramref name="time"/>.</summary>
    public static DateTime LastOccurrence(TimeOnly time, DateTime now)
    {
        var today = now.Date + time.ToTimeSpan();
        return today <= now ? today : today.AddDays(-1);
    }

    public static bool TryParse(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim() ?? "", ["H:mm", "HH:mm"], out time);
}
