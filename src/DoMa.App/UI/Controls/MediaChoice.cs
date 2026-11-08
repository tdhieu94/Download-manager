using DoMa.Core.Models;

namespace DoMa.App.UI.Controls;

/// <summary>Dòng chọn chất lượng video (hoặc chỉ âm thanh MP3) và tải kèm phụ đề, dùng trong hộp tải video từ trang.</summary>
public sealed class MediaChoice : Panel
{
    private readonly ComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
    private readonly CheckBox _subtitles = new() { Text = "Tải kèm phụ đề (Việt, Anh)", AutoSize = true };
    private readonly IReadOnlyList<(string Value, string Label)> _items;

    public event EventHandler? Changed;

    /// <param name="subtitles">false: video không có phụ đề Việt/Anh, ô phụ đề bị tắt.</param>
    public MediaChoice(MediaOptions initial, IReadOnlyList<int>? heights, bool subtitles = true)
    {
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
        Layout += (_, _) =>
        {
            int S(int v) => Theme.Scale(this, v);
            label.Location = new Point(0, (Height - label.Height) / 2);
            _quality.SetBounds(label.Right + S(10), (Height - _quality.Height) / 2, S(200), _quality.Height);
            _subtitles.Location = new Point(_quality.Right + S(18), (Height - _subtitles.Height) / 2);
        };
        _quality.SelectedIndexChanged += (_, _) =>
        {
            _subtitles.Enabled = subtitles && !Options.AudioOnly;
            Changed?.Invoke(this, EventArgs.Empty);
        };
        _subtitles.CheckedChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _subtitles.Enabled = subtitles && !Options.AudioOnly;
    }

    public MediaOptions Options =>
        new(_items[Math.Max(0, _quality.SelectedIndex)].Value, _subtitles.Enabled && _subtitles.Checked);

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
