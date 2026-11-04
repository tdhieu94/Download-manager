using DoMa.App.Capture;
using DoMa.Core.Engine;
using DoMa.Core.Models;
using DialogResult = DoMa.App.Capture.DialogResult;

namespace DoMa.App.UI;

public sealed class NewDownloadDialogHost(SynchronizationContext uiContext) : IDownloadDialog
{
    public Task<DialogResult> ShowAsync(DownloadRequest req, ProbeResult probe, string proposedPath, Category category)
    {
        var done = new TaskCompletionSource<DialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        uiContext.Post(async _ =>
        {
            try
            {
                done.TrySetResult(await new NewDownloadForm().ShowAsync(req, probe, proposedPath, category));
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
        }, null);
        return done.Task;
    }
}
