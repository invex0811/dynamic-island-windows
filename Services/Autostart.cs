using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>
/// Start with Windows via HKCU\...\Run (per user, no admin rights needed), or, in the Store version,
/// through the package's startup task (Settings → Apps → Startup lists it).
/// </summary>
public static class Autostart
{
    private const string TaskId = "AtollStartup";

    private static Windows.ApplicationModel.StartupTask? Task_() =>
        Task.Run(async () => await Windows.ApplicationModel.StartupTask.GetAsync(TaskId)).GetAwaiter().GetResult();

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Windows' own switch for each Run entry (Task Manager › Startup apps, Settings › Apps › Startup):
    // 12 bytes, the first one odd when the entry is switched off there. Without a value the entry is on.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "DynamicIsland";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            if (AppPackage.IsPackaged)
            {
                try { return Task_()?.State is Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy; }
                catch { return false; }
            }
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string value || !string.Equals(value, Command, StringComparison.OrdinalIgnoreCase)) return false;
            // the entry is there, but switched off in Windows' list of startup apps: it won't start
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } flag || (flag[0] & 1) == 0;
        }
    }

    public static void Set(bool enabled)
    {
        if (AppPackage.IsPackaged)
        {
            var task = Task_();
            if (task == null) return;
            if (enabled) Task.Run(async () => await task.RequestEnableAsync()).GetAwaiter().GetResult();
            else task.Disable();
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
        // switched on here: it must not stay switched off in Windows' list; removed: nothing of it stays there
        using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
        approved.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
