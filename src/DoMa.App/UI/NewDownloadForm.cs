using DoMa.App.Capture;
using DoMa.App.UI.Controls;
using DoMa.Core.Engine;
using DoMa.Core.Models;
using DoMa.Core.PostProcess;
using DialogResult = DoMa.App.Capture.DialogResult;

namespace DoMa.App.UI;

public sealed class NewDownloadForm : Form, IDownloadDialog
{
    private readonly InputBox _link = new(IconKind.Link) { ReadOnly = true };
    private readonly InputBox _path = new(IconKind.Folder);
    private readonly ModernButton _browse = new("Chọn…");
    private readonly ModernButton _now = new("Tải ngay", IconKind.Download, ButtonKind.Primary);
    private readonly ModernButton _later = new("Tải sau", IconKind.Clock);
    private readonly ModernButton _cancel = new("Huỷ", IconKind.None, ButtonKind.Ghost);
    private readonly TaskCompletionSource<DialogResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DialogChoice _choice = DialogChoice.Cancel;
    private string _fileName = "";
    private string _sizeText = "";
    private long? _size;
    private Category _category = Category.Other;
    private readonly bool _warn;
    private readonly Panel _body;
    private RiskReport _risk = RiskReport.Safe;

    public NewDownloadForm(bool warnAboutRisk = true)
    {
        _warn = warnAboutRisk;
        Text = "Tải xuống mới";
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        DialogParts.ApplyBase(this, new Size(600, 360));
        ShowInTaskbar = true;
        ShowIcon = true;
        AcceptButton = _now;
        CancelButton = _cancel;

        var body = _body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
        var linkLabel = DialogParts.FieldLabel("Đường dẫn");
        var pathLabel = DialogParts.FieldLabel("Lưu thành");
        var note = DialogParts.Hint("Thư mục tự đổi theo loại file: Tài liệu / Phần mềm / Nén / Khác");

        var inner = ClientSize.Width - S(48);
        linkLabel.Location = new Point(S(24), S(8));
        _link.SetBounds(S(24), S(32), inner, S(38));
        pathLabel.Location = new Point(S(24), S(86));
        _browse.Location = new Point(S(24) + inner - _browse.PreferredSize.Width, S(110));
        _browse.AutoSize = false;
        _browse.Size = _browse.PreferredSize with { Height = S(38) };
        _path.SetBounds(S(24), S(110), inner - _browse.Width - S(8), S(38));
        note.Location = new Point(S(25), S(156));
        body.Controls.AddRange([linkLabel, _link, pathLabel, _path, _browse, note]);

        Controls.Add(body);
        Controls.Add(DialogParts.Footer(this, _now, _later, _cancel));
        Controls.Add(BuildHeader());

        _browse.Click += (_, _) => Browse();
        _now.Click += (_, _) => Finish(DialogChoice.DownloadNow);
        _later.Click += (_, _) => Finish(DialogChoice.Later);
        _cancel.Click += (_, _) => Finish(DialogChoice.Cancel);
        _path.TextChanged += (_, _) =>
        {
            var name = Path.GetFileName(_path.Text.Trim());
            if (name.Length > 0 && name != _fileName)
            {
                _fileName = name;
                Invalidate(true);
            }
        };
        Shown += (_, _) =>
        {
            Activate();
            _path.Box.Focus();
        };
        FormClosed += (_, _) => _result.TrySetResult(new DialogResult(_choice, _path.Text.Trim()));
    }

    private int S(int v) => Theme.Scale(this, v);

    private Panel BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = S(92), BackColor = Theme.Surface };
        header.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var color = Theme.CategoryColor(_category);
            var tile = new RectangleF(S(24), S(22), S(48), S(48));
            Theme.FillRound(g, Theme.Tint(color, 0.87f), tile, S(12));
            Icons.Draw(g, Theme.CategoryIcon(_category), RectangleF.Inflate(tile, -S(12), -S(12)), color);

            var x = S(88);
            var w = header.Width - x - S(24);
            TextRenderer.DrawText(g, _fileName.Length > 0 ? _fileName : "Tệp mới", Theme.Subtitle, new Rectangle(x, S(22), w, S(26)), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            // Hai "chip" thông tin: loại file và dung lượng.
            var chipX = (float)x;
            foreach (var (text, chipColor) in new[] { (Theme.CategoryName(_category), color), (_sizeText, Theme.TextSecondary) })
            {
                var size = TextRenderer.MeasureText(g, text, Theme.SmallBold, Size.Empty, TextFormatFlags.NoPadding);
                var chip = new RectangleF(chipX, S(52), size.Width + S(18), S(22));
                Theme.FillRound(g, chipColor == Theme.TextSecondary ? Theme.Hover : Theme.Tint(chipColor, 0.88f), chip, S(11));
                TextRenderer.DrawText(g, text, Theme.SmallBold, Rectangle.Round(chip), chipColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                chipX = chip.Right + S(8);
            }
        };
        return header;
    }

    public Task<DialogResult> ShowAsync(DownloadRequest req, ProbeResult probe, string proposedPath, Category category, DownloadItem? existing = null)
    {
        _link.Text = req.Url;
        _category = category;
        _sizeText = SizeFormatter.Format(probe.Size ?? req.SizeHint);
        _size = probe.Size ?? req.SizeHint;
        _path.Text = proposedPath;
        _fileName = Path.GetFileName(proposedPath);
        if (existing is not null)
            AddDuplicateBanner(existing);
        if (_warn)
        {
            var name = FirstNonBlank(probe.FileName, req.SuggestedFileName) ?? _fileName;
            _risk = DownloadRisk.Assess(req.Url, probe.FinalUrl, name, probe.ContentType);
            if (_risk.Level != RiskLevel.None)
                AddRiskBanner();
        }
        Show();
        Activate();
        return _result.Task;
    }

    private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>Khung cảnh báo dưới ô đường dẫn; cửa sổ cao thêm cho vừa.</summary>
    private void AddRiskBanner()
    {
        var high = _risk.Level == RiskLevel.High;
        var color = high ? Theme.Danger : Theme.Warning;
        var title = high ? "Tệp này có dấu hiệu nguy hiểm" : "Hãy cẩn thận với tệp này";
        AddBanner(title, string.Join("\n", _risk.Reasons.Select(r => "• " + r)), color, IconKind.Shield, null);

        if (high)
        {
            // Với tệp nguy hiểm, Enter không được tự tải: phải bấm hẳn vào nút.
            _now.Text = "Vẫn tải";
            AcceptButton = null;
        }
    }

    /// <summary>Nhắc tệp đã có trong danh sách (đã tải xong hoặc đang tải dở) để khỏi tải trùng.</summary>
    private void AddDuplicateBanner(DownloadItem existing)
    {
        var name = Path.GetFileName(existing.TargetPath);
        var done = existing.Status == DownloadStatus.Completed;
        var exists = done && File.Exists(existing.TargetPath);
        var when = (existing.CompletedAt ?? existing.AddedAt).ToLocalTime().ToString("HH:mm dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        var (title, text) = (done, exists) switch
        {
            (true, true) => ("Bạn đã tải tệp này rồi", $"“{name}” tải xong lúc {when}. Bấm Tải ngay nếu vẫn muốn tải thêm một bản."),
            (true, false) => ("Bạn đã từng tải tệp này", $"“{name}” tải xong lúc {when}, nhưng tệp đã bị xoá hoặc chuyển đi."),
            _ => ("Tệp này đang có trong danh sách", $"“{name}” đang ở trạng thái {StatusText(existing.Status)}. Có thể bấm Huỷ và tải tiếp tệp đó trong cửa sổ DoMa."),
        };
        // Tệp bị đánh dấu mã độc thì không mở thẳng từ đây.
        (string, Action)? action = exists && existing.Threat is null ? ("Mở tệp", () => MainForm.OpenFile(existing.TargetPath)) : null;
        AddBanner(title, text, Theme.Accent, IconKind.File, action);
    }

    private static string StatusText(DownloadStatus status) => status switch
    {
        DownloadStatus.Downloading => "đang tải",
        DownloadStatus.Queued => "chờ tải",
        DownloadStatus.Paused => "tạm dừng",
        DownloadStatus.Failed => "lỗi",
        DownloadStatus.Later => "chờ tải sau",
        _ => "chưa xong",
    };

    private int _bannerTop;

    /// <summary>Một khung thông báo dưới ô đường dẫn (xếp chồng nếu có nhiều); cửa sổ cao thêm cho vừa.</summary>
    private void AddBanner(string title, string text, Color color, IconKind icon, (string Text, Action Run)? action)
    {
        if (_bannerTop == 0)
            _bannerTop = S(186);
        var inner = _body.ClientSize.Width - S(48);
        var textWidth = inner - S(56) - (action is null ? 0 : S(96));
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        var textHeight = TextRenderer.MeasureText(text, Theme.Small, new Size(textWidth, int.MaxValue), flags).Height;
        var height = S(40) + textHeight + S(14);

        var banner = new Panel { BackColor = Theme.Surface };
        banner.SetBounds(S(24), _bannerTop, inner, height);
        banner.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new RectangleF(0, 0, banner.Width - 1, banner.Height - 1);
            Theme.FillRound(g, Theme.Tint(color, 0.88f), r, S(10));
            Theme.DrawRound(g, Theme.Tint(color, 0.55f), r, S(10));
            Icons.Draw(g, icon, new RectangleF(S(14), S(12), S(24), S(24)), color);
            TextRenderer.DrawText(g, title, Theme.BodySemibold, new Rectangle(S(48), S(14), textWidth, S(22)), Theme.Readable(color), flags);
            TextRenderer.DrawText(g, text, Theme.Small, new Rectangle(S(48), S(40), textWidth, textHeight), Theme.Text, flags);
        };
        if (action is { } a)
        {
            var button = new ModernButton(a.Text) { AutoSize = false };
            button.Size = button.PreferredSize with { Height = S(32) };
            button.Location = new Point(inner - button.Width - S(12), (height - button.Height) / 2);
            button.Click += (_, _) => a.Run();
            banner.Controls.Add(button);
        }
        _body.Controls.Add(banner);
        _bannerTop += height + S(12);
        Height += height + S(12);
    }

    private void Browse()
    {
        using var dialog = new SaveFileDialog
        {
            FileName = Path.GetFileName(_path.Text),
            InitialDirectory = Path.GetDirectoryName(_path.Text),
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
            _path.Text = dialog.FileName;
    }

    private static bool IsValidPath(string path) =>
        path.Length > 0
        && path.IndexOfAny(Path.GetInvalidPathChars()) < 0
        && Path.IsPathRooted(path)
        && Path.GetFileName(path).Length > 0
        && Path.GetFileName(path).IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private void Finish(DialogChoice choice)
    {
        if (choice != DialogChoice.Cancel && !IsValidPath(_path.Text.Trim()))
        {
            MessageBox.Show(this, "Đường dẫn lưu không hợp lệ.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _path.Box.Focus();
            return;
        }
        if (choice == DialogChoice.DownloadNow && Core.Files.DiskSpace.Shortage(_path.Text.Trim(), _size) is { } shortage)
        {
            var answer = MessageBox.Show(this,
                shortage + ".\n\nHãy bấm “Chọn…” để lưu sang ổ khác, hoặc dọn bớt ổ rồi tải. Vẫn thêm vào danh sách “Chờ tải sau”?",
                "Không đủ chỗ trống", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != System.Windows.Forms.DialogResult.Yes)
                return;
            choice = DialogChoice.Later;
        }
        if (choice != DialogChoice.Cancel && _risk.Level == RiskLevel.High)
        {
            var answer = MessageBox.Show(this,
                "DoMa phát hiện tệp này có dấu hiệu nguy hiểm:\n\n" + string.Join("\n", _risk.Reasons.Select(r => "• " + r))
                + "\n\nBạn vẫn muốn tải? Sau khi tải nên quét bằng Windows Defender trước khi mở.",
                "Cảnh báo bảo mật", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != System.Windows.Forms.DialogResult.Yes)
                return;
        }
        _choice = choice;
        Close();
    }
}
