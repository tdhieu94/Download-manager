using DoMa.App.UI.Controls;

namespace DoMa.App.UI;

/// <summary>Hộp thoại nhập mức tốc độ tối đa tuỳ ý cho một tệp.</summary>
public sealed class SpeedLimitForm : Form
{
    private readonly InputBox _value = new(IconKind.Gauge) { PlaceholderText = L.T("Ví dụ: 750, 1.5 MB") };

    public SpeedLimitForm(int currentKBps)
    {
        Text = L.T("Giới hạn tốc độ");
        StartPosition = FormStartPosition.CenterParent;
        DialogParts.ApplyBase(this, new Size(460, 236));
        int S(int v) => Theme.Scale(this, v);

        var ok = new ModernButton(L.T("Đặt"), IconKind.Check, ButtonKind.Primary) { DialogResult = DialogResult.OK };
        var cancel = new ModernButton(L.T("Huỷ")) { DialogResult = DialogResult.Cancel };
        AcceptButton = ok;
        CancelButton = cancel;

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(24), S(4), S(24), 0) };
        var label = DialogParts.FieldLabel(L.T("Tốc độ tối đa (KB/s, hoặc ghi MB)"));
        label.Location = new Point(S(24), S(6));
        _value.Text = currentKBps > 0 ? currentKBps.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        _value.SetBounds(S(24), S(32), ClientSize.Width - S(48), S(38));
        _value.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        body.Controls.Add(label);
        body.Controls.Add(_value);

        Controls.Add(body);
        Controls.Add(DialogParts.Footer(this, ok, cancel));
        Controls.Add(DialogParts.Header(this, IconKind.Gauge, Theme.Accent, L.T("Giới hạn tốc độ tệp này"), L.T("Để trống hoặc nhập 0 để bỏ giới hạn riêng.")));

        ok.Click += (_, _) =>
        {
            if (DownloadView.ParseSpeedKBps(_value.Text) is not null)
                return;
            MessageBox.Show(this, L.T("Hãy nhập một số, ví dụ 750 hoặc 1.5 MB."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            _value.Box.Focus();
        };
        Shown += (_, _) =>
        {
            _value.Box.Focus();
            _value.Box.SelectAll();
        };
    }

    /// <summary>Mức đã nhập, KB/s (0 = bỏ giới hạn riêng).</summary>
    public int KBps => DownloadView.ParseSpeedKBps(_value.Text) ?? 0;
}
