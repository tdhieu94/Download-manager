using DoMa.App.Scheduling;
using DoMa.App.UI.Controls;

namespace DoMa.App.UI;

/// <summary>Đếm ngược 60 giây trước khi cho máy ngủ / tắt máy, để người dùng kịp huỷ.</summary>
public sealed class PowerCountdownForm : Form
{
    private const int Seconds = 60;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Label _label = new() { AutoSize = false, Font = Theme.Body, ForeColor = Theme.TextSecondary };
    private int _left = Seconds;

    internal PowerCountdownForm(AfterDone action)
    {
        var verb = action == AfterDone.Sleep ? "cho máy ngủ" : "tắt máy";
        Text = "Đã tải xong";
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        DialogParts.ApplyBase(this, new Size(460, 220));
        ShowInTaskbar = true;
        int S(int v) => Theme.Scale(this, v);

        var go = new ModernButton(action == AfterDone.Sleep ? "Ngủ ngay" : "Tắt máy ngay", IconKind.Power, ButtonKind.Primary) { DialogResult = DialogResult.OK };
        var cancel = new ModernButton("Huỷ") { DialogResult = DialogResult.Cancel };
        AcceptButton = go;
        CancelButton = cancel;

        _label.SetBounds(S(24), S(4), ClientSize.Width - S(48), S(44));
        var body = new Panel { Dock = DockStyle.Fill };
        body.Controls.Add(_label);
        Controls.Add(body);
        Controls.Add(DialogParts.Footer(this, go, cancel));
        Controls.Add(DialogParts.Header(this, IconKind.Power, Theme.Warning, "Mọi tệp đã tải xong", $"DoMa sẽ {verb} khi hết thời gian đếm ngược."));

        Render(verb);
        _timer.Tick += (_, _) =>
        {
            _left--;
            Render(verb);
            if (_left <= 0)
            {
                _timer.Stop();
                DialogResult = DialogResult.OK;
            }
        };
        Shown += (_, _) => _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();
    }

    private void Render(string verb) => _label.Text = $"Sẽ {verb} sau {_left} giây. Bấm “Huỷ” nếu bạn vẫn đang dùng máy.";
}
