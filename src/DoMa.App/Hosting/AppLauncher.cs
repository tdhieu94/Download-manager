using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace DoMa.App.Hosting;

public static class AppLauncher
{
    private const uint DetachedProcess = 0x00000008;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const int ErrorAccessDenied = 5;

    public static void StartDetached(string exePath, string args)
    {
        var error = Create(exePath, args, DetachedProcess | CreateBreakawayFromJob);
        if (error == ErrorAccessDenied)
            error = Create(exePath, args, DetachedProcess);
        if (error != 0)
            throw new Win32Exception(error);
    }

    private static int Create(string exePath, string args, uint flags)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var commandLine = new StringBuilder($"\"{exePath}\" {args}");
        if (!CreateProcess(exePath, commandLine, 0, 0, false, flags, 0, Path.GetDirectoryName(exePath), ref startup, out var info))
            return Marshal.GetLastPInvokeError();
        CloseHandle(info.Process);
        CloseHandle(info.Thread);
        return 0;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }
}
