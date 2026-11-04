using DoMa.App.UI.Controls;

namespace DoMa.App.UI;

/// <summary>Cho xem nội dung bản cập nhật (tiếng Việt trước, tiếng Anh sau) trước khi cài.</summary>
public sealed class UpdateNotesForm : Form
{
    public UpdateNotesForm(string version, string currentVersion, string notes)
    {
        Text = "Có gì mới";
        StartPosition = FormStartPosition.CenterParent;
        DialogParts.ApplyBase(this, new Size(620, 520));
        int S(int v) => Theme.Scale(this, v);

        var install = new ModernButton("Cập nhật ngay", IconKind.Download, ButtonKind.Primary) { DialogResult = DialogResult.OK };
        var close = new ModernButton("Để sau") { DialogResult = DialogResult.Cancel };
        AcceptButton = install;
        CancelButton = close;

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(S(24), S(4), S(24), S(12)) };
        var frame = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SurfaceAlt, Padding = new Padding(S(14), S(12), S(6), S(12)) };
        var text = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = Theme.Body,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.Text,
            TabStop = false,
            Text = string.IsNullOrWhiteSpace(notes)
                ? "Bản này chưa có ghi chú cập nhật.\r\nNo release notes for this version."
                : notes.Replace("\n", "\r\n"),
        };
        Theme.StyleScrollbars(text);
        frame.Paint += (_, e) => Theme.DrawRound(e.Graphics, Theme.Border, new RectangleF(0, 0, frame.Width - 1, frame.Height - 1), S(8));
        frame.Controls.Add(text);
        body.Controls.Add(frame);

        Controls.Add(body);
        Controls.Add(DialogParts.Footer(this, install, close));
        Controls.Add(DialogParts.Header(this, IconKind.Download, Theme.Accent, $"DoMa {version}",
            $"Bạn đang dùng bản {currentVersion}. Các tệp đang tải sẽ tự tải tiếp sau khi cập nhật."));
        Shown += (_, _) => install.Focus();
    }
}
