using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DoMa.App.Ipc;

public static class PipeClient
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    public static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, int timeoutMs, CancellationToken ct)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(timeoutMs, ct);
            if (!IsServerCurrentUser(client))
                throw new UnauthorizedAccessException("Pipe server does not run as the current user");
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static bool IsServerCurrentUser(NamedPipeClientStream client)
    {
        if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out var processId))
            return false;
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token))
            return false;
        using (token)
        {
            using var server = new WindowsIdentity(token.DangerousGetHandle());
            using var current = WindowsIdentity.GetCurrent();
            return server.User is not null && server.User == current.User;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
