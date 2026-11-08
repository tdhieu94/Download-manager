using DoMa.App.UI.Controls;

namespace DoMa.App.UI;

/// <summary>Hộp thoại đổi tên một tệp trong danh sách (đã tải xong hoặc đang dở).</summary>
public sealed class RenameForm : Form
{
    private readonly InputBox _name = new(IconKind.File);

    public RenameForm(string current)
    {
        Text = L.T("Đổi tên");
        StartPosition = FormStartPosition.CenterParent;
        DialogParts.ApplyBase(this, new Size(520, 236));
        int S(int v) => Theme.Scale(this, v);

        var ok = new ModernButton(L.T("Đổi tên"), IconKind.Check, ButtonKind.Primary) { DialogResult = DialogResult.OK };
        var cancel = new ModernButton(L.T("Huỷ")) { DialogResult = DialogResult.Cancel };
        AcceptButton = ok;
        CancelButton = cancel;

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(24), S(4), S(24), 0) };
        var label = DialogParts.FieldLabel(L.T("Tên mới"));
        label.Location = new Point(S(24), S(6));
        _name.Text = current;
        _name.SetBounds(S(24), S(32), ClientSize.Width - S(48), S(38));
        _name.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        body.Controls.Add(label);
        body.Controls.Add(_name);

        Controls.Add(body);
        Controls.Add(DialogParts.Footer(this, ok, cancel));
        Controls.Add(DialogParts.Header(this, IconKind.File, Theme.Accent, L.T("Đổi tên tệp"), L.T("Tệp trên ổ đĩa cũng được đổi tên theo.")));

        Shown += (_, _) =>
        {
            // Chọn sẵn phần tên, không chọn đuôi, như Windows Explorer.
            _name.Box.Focus();
            var stem = Path.GetFileNameWithoutExtension(current).Length;
            _name.Box.Select(0, stem > 0 ? stem : current.Length);
        };
    }

    public string NewName => _name.Text.Trim();
}
