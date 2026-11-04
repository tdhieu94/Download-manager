using Microsoft.Win32;

namespace DoMa.App;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DoMa";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --nen");
        else
            key.DeleteValue(ValueName, false);
    }
}
