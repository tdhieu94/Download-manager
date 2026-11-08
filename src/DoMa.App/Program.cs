using System.Text.Json.Nodes;
using DoMa.App.Hosting;
using DoMa.App.Ipc;
using DoMa.Core.Messaging;

namespace DoMa.App;

internal static class Program
{
    private const string MutexName = @"Local\DoMa.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        if (NativeHostRelay.IsHostInvocation(args))
            RunHost();
        else
            RunApp(args.Contains("--nen"));
    }

    private static void RunHost()
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        new NativeHostRelay(stdin, stdout, ConnectOrLaunchAsync).RunAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private static async Task<Stream> ConnectOrLaunchAsync(CancellationToken ct)
    {
        if (Mutex.TryOpenExisting(MutexName, out var running))
            running.Dispose();
        else
            AppLauncher.StartDetached(Environment.ProcessPath!, "--nen");
        return await PipeClient.ConnectAsync(PipeServer.CurrentUserPipeName, 5000, ct);
    }

    private static void RunApp(bool hidden)
    {
        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            if (!hidden)
                SendShow();
            return;
        }

        ApplicationConfiguration.Initialize();
        var context = new MainContext(hidden);
        var server = new PipeServer(PipeServer.CurrentUserPipeName);
        server.MessageReceived += context.OnMessage;
        using var cts = new CancellationTokenSource();
        try
        {
            server.StartAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(L.T("DoMa không khởi động được: kênh liên lạc đang bị chương trình khác chiếm."), "Download Manager (DoMa)", MessageBoxButtons.OK, MessageBoxIcon.Error);
            context.Exit();
            return;
        }
        Application.Run(context);
        cts.Cancel();
    }

    private static void SendShow()
    {
        try
        {
            using var client = PipeClient.ConnectAsync(PipeServer.CurrentUserPipeName, 2000, CancellationToken.None).GetAwaiter().GetResult();
            FrameCodec.WriteAsync(client, new JsonObject { ["type"] = MessageTypes.Show }, CancellationToken.None).GetAwaiter().GetResult();
            client.WaitForPipeDrain();
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
        }
    }
}
