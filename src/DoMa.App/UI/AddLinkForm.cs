using DoMa.App.UI.Controls;

namespace DoMa.App.UI;

/// <summary>
/// Hộp thoại dán đường dẫn tải thủ công. Cũng dùng cho "Làm mới link": khi đó có thêm nút phụ
/// (vd. "Mở trang nguồn"), bấm nút này hộp thoại trả về <see cref="DialogResult.Retry"/>.
/// </summary>
public sealed class AddLinkForm : Form
{
    private readonly InputBox _link = new(IconKind.Link) { PlaceholderText = "https://…" };

    public AddLinkForm(string title = "Thêm link tải", string hint = "Dán đường dẫn http hoặc https của tệp bạn muốn tải.",
        string okText = "Tiếp tục", string? secondaryText = null)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        DialogParts.ApplyBase(this, new Size(560, 236));
        int S(int v) => Theme.Scale(this, v);

        var ok = new ModernButton(okText, IconKind.Download, ButtonKind.Primary) { DialogResult = DialogResult.OK };
        var cancel = new ModernButton("Huỷ") { DialogResult = DialogResult.Cancel };
        AcceptButton = ok;
        CancelButton = cancel;

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(24), S(4), S(24), 0) };
        var label = DialogParts.FieldLabel("Đường dẫn tải");
        label.Location = new Point(S(24), S(6));
        _link.SetBounds(S(24), S(32), ClientSize.Width - S(48), S(38));
        _link.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        body.Controls.Add(label);
        body.Controls.Add(_link);

        Controls.Add(body);
        Button[] buttons = secondaryText is null
            ? [ok, cancel]
            : [ok, new ModernButton(secondaryText, IconKind.Link) { DialogResult = DialogResult.Retry }, cancel];
        Controls.Add(DialogParts.Footer(this, buttons));
        Controls.Add(DialogParts.Header(this, IconKind.Link, Theme.Accent, title, hint));

        Shown += (_, _) =>
        {
            if (_link.Text.Length == 0 && TryClipboardUrl() is { } url)
                _link.Text = url;
            _link.Box.Focus();
            _link.Box.SelectAll();
        };
    }

    public string Link => _link.Text.Trim();

    private static string? TryClipboardUrl()
    {
        try
        {
            if (!Clipboard.ContainsText())
                return null;
            var text = Clipboard.GetText().Trim();
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? text : null;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }
}
