using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>
/// "Only on the island": Windows pops up its own banner for every message the island already shows.
/// Windows keeps a per-app switch for that banner (Settings → System → Notifications → the app →
/// "Show notification banners"); turning it off leaves the message in the notification center, where
/// the island reads it, and only the pop-up goes away. We flip that switch for the apps the island
/// mirrors, remember what each one had, and put it back when the island closes or the option is turned off.
/// </summary>
public static class WindowsBanners
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private const string Value = "ShowBanner";

    /// <summary>App id → what "ShowBanner" was before us ("" = not set, "0", "1").</summary>
    private static Dictionary<string, string> Changed => Settings.Current.HiddenBanners;

    /// <summary>Hide this app's banner (its key exists once it has sent a notification). Remembers the old value.</summary>
    public static void Hide(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || Settings.ReadOnly) return;
        try
        {
            // only apps Windows already knows: never create settings for something that isn't there
            using var k = Registry.CurrentUser.OpenSubKey(Key + "\\" + appId, writable: true);
            if (k == null) return;
            var now = k.GetValue(Value) as int?;
            if (!Changed.ContainsKey(appId))
            {
                Changed[appId] = now?.ToString() ?? "";
                Settings.Current.Save();
            }
            if (now != 0) k.SetValue(Value, 0, RegistryValueKind.DWord);
        }
        catch (Exception e) { Log.Write("banners: " + e.Message); }
    }

    /// <summary>Hide again everything we hid before (after a start), plus the known apps that match.</summary>
    public static void HideKnown(Func<string, bool> alsoMatching)
    {
        if (Settings.ReadOnly) return;
        foreach (var id in Changed.Keys.ToList()) Hide(id);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Key);
            if (root == null) return;
            foreach (var id in root.GetSubKeyNames())
                if (!Changed.ContainsKey(id) && alsoMatching(id)) Hide(id);
        }
        catch (Exception e) { Log.Write("banners: " + e.Message); }
    }

    /// <summary>
    /// Put the banners back the way they were. forget: the option was turned off (or the app is being removed),
    /// so the list is dropped; otherwise it is kept and applied again on the next start.
    /// </summary>
    public static void Restore(bool forget, Func<string, bool>? only = null)
    {
        if (Settings.ReadOnly || Changed.Count == 0) return;
        foreach (var (id, was) in Changed.ToList())
        {
            if (only != null && !only(id)) continue;
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key + "\\" + id, writable: true);
                if (k != null)
                {
                    if (was == "") k.DeleteValue(Value, throwOnMissingValue: false);
                    else k.SetValue(Value, was == "0" ? 0 : 1, RegistryValueKind.DWord);
                }
            }
            catch (Exception e) { Log.Write("banners: " + e.Message); }
            if (forget) Changed.Remove(id);
        }
        if (forget) Settings.Current.Save();
    }
}
