using DoMa.App.UI.Controls;
using DoMa.Core.PostProcess;

namespace DoMa.App.UI;

/// <summary>Tính MD5 / SHA-1 / SHA-256 của tệp đã tải và so với mã băm do trang web công bố.</summary>
public sealed class HashForm : Form
{
    private readonly string _path;
    private readonly InputBox _md5 = new() { ReadOnly = true };
    private readonly InputBox _sha1 = new() { ReadOnly = true };
    private readonly InputBox _sha256 = new() { ReadOnly = true };
    private readonly InputBox _expected = new(IconKind.Hash) { PlaceholderText = "Dán mã băm từ trang tải để so sánh" };
    private readonly Label _verdict = new() { AutoSize = false, Font = Theme.BodySemibold };
    private readonly CancellationTokenSource _cts = new();
    private FileHashes? _hashes;

    public HashForm(string path)
    {
        _path = path;
        Text = "Kiểm tra mã băm";
        StartPosition = FormStartPosition.CenterParent;
        DialogParts.ApplyBase(this, new Size(640, 470));
        int S(int v) => Theme.Scale(this, v);

        var close = new ModernButton("Đóng") { DialogResult = DialogResult.Cancel };
        CancelButton = close;

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
        var x = S(24);
        var w = ClientSize.Width - S(48);
        var y = S(4);
        foreach (var (label, box) in new[] { ("SHA-256", _sha256), ("SHA-1", _sha1), ("MD5", _md5) })
        {
            var caption = DialogParts.FieldLabel(label);
            caption.Location = new Point(x, y + S(9));
            box.SetBounds(x + S(80), y, w - S(80) - S(44), S(36));
            box.Text = "Đang tính…";
            var copy = new ModernButton("Copy", IconKind.Copy, ButtonKind.Ghost) { IconOnly = true, AutoSize = false };
            copy.SetBounds(x + w - S(36), y + 1, S(34), S(34));
            copy.Click += (_, _) =>
            {
                if (_hashes is not null)
                    TrySetClipboard(box.Text);
            };
            body.Controls.AddRange([caption, box, copy]);
            y += S(46);
        }
        y += S(8);
        var expectedCaption = DialogParts.FieldLabel("So sánh với");
        expectedCaption.Location = new Point(x, y);
        y += S(24);
        _expected.SetBounds(x, y, w, S(38));
        y += S(48);
        _verdict.SetBounds(x, y, w, S(24));
        body.Controls.AddRange([expectedCaption, _expected, _verdict]);

        Controls.Add(body);
        Controls.Add(DialogParts.Footer(this, close));
        Controls.Add(DialogParts.Header(this, IconKind.Shield, Theme.Success, Path.GetFileName(path), "Đối chiếu mã băm để chắc tệp không bị hỏng hay bị tráo."));

        _expected.TextChanged += (_, _) => Compare();
        Shown += async (_, _) => await ComputeAsync();
        FormClosed += (_, _) => _cts.Cancel();
    }

    private async Task ComputeAsync()
    {
        try
        {
            var progress = new Progress<double>(p => _sha256.Text = $"Đang tính… {p:P0}");
            _hashes = await FileHasher.ComputeAsync(_path, progress, _cts.Token);
            _md5.Text = _hashes.Md5;
            _sha1.Text = _hashes.Sha1;
            _sha256.Text = _hashes.Sha256;
            Compare();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _md5.Text = _sha1.Text = _sha256.Text = "";
            _verdict.ForeColor = Theme.Danger;
            _verdict.Text = "Không đọc được tệp: " + e.Message;
        }
    }

    private void Compare()
    {
        if (_hashes is null || _expected.Text.Trim().Length == 0)
        {
            _verdict.Text = "";
            return;
        }
        var match = FileHasher.Match(_expected.Text, _hashes);
        _verdict.ForeColor = match is null ? Theme.Danger : Theme.Success;
        _verdict.Text = match is null ? "✗  Không khớp — tệp có thể bị hỏng hoặc không đúng bản gốc." : $"✓  Khớp {match}. Tệp đúng với bản gốc.";
    }

    private static void TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }
    }
}
