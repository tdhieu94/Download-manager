using System.Runtime.InteropServices;
using DoMa.Core.Files;

namespace DoMa.App.Capture;

/// <summary>
/// Theo dõi clipboard của Windows (AddClipboardFormatListener). Khi người dùng copy một link tải,
/// gọi onLink để DoMa đề nghị tải. Không đọc gì khi tính năng bị tắt.
/// </summary>
internal sealed class ClipboardWatcher : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static string? _ignore;

    private readonly Func<bool> _enabled;
    private readonly Func<IEnumerable<string>> _extensions;
    private readonly Action<string> _onLink;
    private string? _last;
    private DateTime _lastAt;

    public ClipboardWatcher(Func<bool> enabled, Func<IEnumerable<string>> extensions, Action<string> onLink)
    {
        _enabled = enabled;
        _extensions = extensions;
        _onLink = onLink;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE: cửa sổ ẩn chỉ nhận thông điệp.
        AddClipboardFormatListener(Handle);
    }

    /// <summary>Bỏ qua lần copy link này (do chính DoMa copy, vd. "Copy link").</summary>
    public static void IgnoreOnce(string text) => _ignore = text.Trim();

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
            OnClipboardChanged();
        base.WndProc(ref m);
    }

    private void OnClipboardChanged()
    {
        if (!_enabled())
            return;
        string text;
        try
        {
            if (!Clipboard.ContainsText())
                return;
            text = Clipboard.GetText().Trim();
        }
        catch (ExternalException)
        {
            return; // Ứng dụng khác đang giữ clipboard.
        }
        if (text == _ignore)
        {
            _ignore = null;
            return;
        }
        if (ClipboardLinks.Extract(text, _extensions()) is not { } url)
            return;
        // Một số ứng dụng báo thay đổi clipboard nhiều lần cho một lần copy.
        if (url == _last && DateTime.UtcNow - _lastAt < TimeSpan.FromSeconds(5))
            return;
        _last = url;
        _lastAt = DateTime.UtcNow;
        _onLink(url);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
