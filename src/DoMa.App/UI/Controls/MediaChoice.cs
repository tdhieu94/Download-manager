using DoMa.Core.Models;

namespace DoMa.App.UI.Controls;

/// <summary>Dòng chọn chất lượng video (hoặc chỉ âm thanh MP3) và tải kèm phụ đề, dùng trong hộp tải video từ trang.</summary>
public sealed class MediaChoice : Panel
{
    private readonly ComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
    private readonly CheckBox _subtitles = new() { Text = "Tải kèm phụ đề (Việt, Anh)", AutoSize = true };
    private readonly IReadOnlyList<(string Value, string Label)> _items;
    private readonly CheckBox? _clip;
    private readonly TextBox? _from;
    private readonly TextBox? _to;
    private readonly int? _duration;

    /// <summary>Chiều cao cần có: một dòng, hoặc hai dòng khi có ô cắt đoạn.</summary>
    public static int RowsHeight(Control scale, bool clip) => Theme.Scale(scale, clip ? 76 : 36);

    public event EventHandler? Changed;

    /// <param name="subtitles">false: video không có phụ đề Việt/Anh, ô phụ đề bị tắt.</param>
    /// <param name="clip">Hiện thêm dòng "Chỉ tải đoạn" (một video, biết <paramref name="duration"/> giây).</param>
    public MediaChoice(MediaOptions initial, IReadOnlyList<int>? heights, bool subtitles = true, bool clip = false, int? duration = null)
    {
        _duration = duration;
        BackColor = Theme.Surface;
        _items = MediaQualities.For(heights);
        var label = new Label { Text = "Chất lượng", AutoSize = true, Font = Theme.BodySemibold, ForeColor = Theme.Text };
        _quality.Font = Theme.Body;
        _quality.BackColor = Theme.Surface;
        _quality.ForeColor = Theme.Text;
        _quality.Items.AddRange(_items.Select(i => (object)i.Label).ToArray());
        var index = Pick(initial.Quality, heights);
        _quality.SelectedIndex = index;
        _subtitles.Font = Theme.Body;
        _subtitles.ForeColor = Theme.Text;
        _subtitles.Enabled = subtitles;
        _subtitles.Checked = subtitles && initial.Subtitles;
        Controls.AddRange([label, _quality, _subtitles]);
        Label? toLabel = null;
        if (clip)
        {
            _clip = new CheckBox { Text = "Chỉ tải đoạn từ", AutoSize = true, Font = Theme.Body, ForeColor = Theme.Text };
            _from = new TextBox { Text = "0:00", Font = Theme.Body, BackColor = Theme.Surface, ForeColor = Theme.Text, Enabled = false, TextAlign = HorizontalAlignment.Center };
            _to = new TextBox { Text = duration is { } d ? MediaOptions.FormatTime(d) : "", PlaceholderText = "hết", Font = Theme.Body, BackColor = Theme.Surface, ForeColor = Theme.Text, Enabled = false, TextAlign = HorizontalAlignment.Center };
            toLabel = new Label { Text = "đến", AutoSize = true, Font = Theme.Body, ForeColor = Theme.Text };
            var hint = new Label { Text = "(phút:giây)", AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextMuted, Name = "hint" };
            _clip.CheckedChanged += (_, _) =>
            {
                _from.Enabled = _to.Enabled = _clip.Checked;
                Changed?.Invoke(this, EventArgs.Empty);
            };
            Controls.AddRange([_clip, _from, toLabel, _to, hint]);
        }
        Layout += (_, _) =>
        {
            int S(int v) => Theme.Scale(this, v);
            var row = S(36);
            label.Location = new Point(0, (row - label.Height) / 2);
            _quality.SetBounds(label.Right + S(10), (row - _quality.Height) / 2, S(200), _quality.Height);
            _subtitles.Location = new Point(_quality.Right + S(18), (row - _subtitles.Height) / 2);
            if (_clip is null || _from is null || _to is null || toLabel is null)
                return;
            var y = row + S(4);
            _clip.Location = new Point(0, y + (row - _clip.Height) / 2);
            _from.SetBounds(_clip.Right + S(6), y + (row - _from.Height) / 2, S(80), _from.Height);
            toLabel.Location = new Point(_from.Right + S(8), y + (row - toLabel.Height) / 2);
            _to.SetBounds(toLabel.Right + S(8), y + (row - _to.Height) / 2, S(80), _to.Height);
            if (Controls["hint"] is { } h)
                h.Location = new Point(_to.Right + S(10), y + (row - h.Height) / 2);
        };
        _quality.SelectedIndexChanged += (_, _) =>
        {
            _subtitles.Enabled = subtitles && !Options.AudioOnly;
            Changed?.Invoke(this, EventArgs.Empty);
        };
        _subtitles.CheckedChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _subtitles.Enabled = subtitles && !Options.AudioOnly;
    }

    public MediaOptions Options
    {
        get
        {
            var options = new MediaOptions(_items[Math.Max(0, _quality.SelectedIndex)].Value, _subtitles.Enabled && _subtitles.Checked);
            if (_clip is not { Checked: true } || Error is not null)
                return options;
            var start = MediaOptions.ParseTime(_from!.Text) ?? 0;
            var end = MediaOptions.ParseTime(_to!.Text);
            if (end is { } e && _duration is { } d && e >= d)
                end = null;
            return options with { ClipStart = start > 0 ? start : null, ClipEnd = end };
        }
    }

    /// <summary>Câu báo lỗi nếu thời điểm cắt đoạn không hợp lệ; null nếu ổn.</summary>
    public string? Error
    {
        get
        {
            if (_clip is not { Checked: true })
                return null;
            var start = MediaOptions.ParseTime(_from!.Text);
            var endText = _to!.Text.Trim();
            var end = endText.Length == 0 ? null : MediaOptions.ParseTime(endText);
            if (start is null || (endText.Length > 0 && end is null))
                return "Thời điểm cắt đoạn phải có dạng phút:giây, ví dụ 1:30.";
            if (end is { } e && e <= start)
                return "Thời điểm kết thúc phải sau thời điểm bắt đầu.";
            if (_duration is { } d && start >= d)
                return $"Video chỉ dài {MediaOptions.FormatTime(d)}.";
            return null;
        }
    }

    /// <summary>Chọn lại mức đã dùng lần trước; nếu video không có mức đó thì lấy mức gần nhất thấp hơn.</summary>
    private int Pick(string quality, IReadOnlyList<int>? heights)
    {
        var exact = _items.ToList().FindIndex(i => i.Value == quality);
        if (exact >= 0)
            return exact;
        if (int.TryParse(quality, out var wanted))
        {
            if (heights is { Count: > 0 } && heights.Max() <= wanted)
                return 0;
            var lower = _items.ToList().FindIndex(i => int.TryParse(i.Value, out var h) && h <= wanted);
            if (lower >= 0)
                return lower;
        }
        return 0;
    }
}
