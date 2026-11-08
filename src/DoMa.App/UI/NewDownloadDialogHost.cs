using DoMa.App.Capture;
using DoMa.Core.Engine;
using DoMa.Core.Models;
using DialogResult = DoMa.App.Capture.DialogResult;

namespace DoMa.App.UI;

/// <param name="lastMedia">Chất lượng video chọn lần trước; <paramref name="remember"/> lưu lại lựa chọn mới.</param>
public sealed class NewDownloadDialogHost(SynchronizationContext uiContext, Func<bool> warnAboutRisk,
    Func<MediaOptions>? lastMedia = null, Action<MediaOptions>? remember = null) : IDownloadDialog
{
    public Task<DialogResult> ShowAsync(DownloadRequest req, ProbeResult probe, string proposedPath, Category category, DownloadItem? existing = null)
    {
        var done = new TaskCompletionSource<DialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        uiContext.Post(async _ =>
        {
            try
            {
                var result = await new NewDownloadForm(warnAboutRisk(), lastMedia?.Invoke()).ShowAsync(req, probe, proposedPath, category, existing);
                if (result.Media is { } media && result.Choice != DialogChoice.Cancel)
                    remember?.Invoke(media);
                done.TrySetResult(result);
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
        }, null);
        return done.Task;
    }
}
