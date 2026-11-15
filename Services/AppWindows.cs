using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DynamicIsland.Services;

/// <summary>
/// Finds the window of the program a notification came from and brings it forward.
/// Windows names the sender by its app id (the id of its Start menu entry). A window belongs to it when,
/// strongest first: its process is that Store app; its program is the file that Start menu entry starts
/// (or the same file deeper in that folder: Discord's entry starts a stub next to "app-1.0.9\Discord.exe");
/// or, as a last resort, the program's name fits the first word of the app's name.
/// (The name alone is weak: "Google Chrome" runs as chrome.exe, translated names match nothing, and
/// "Discord" fits Discord Canary too. So where the entry's file is known, the name only counts inside its folder.)
/// </summary>
internal static class AppWindows
{
    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr p, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr p, ref int length, StringBuilder id);

    private const uint GW_OWNER = 4, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int SW_RESTORE = 9, DWMWA_CLOAKED = 14;

    // ---- the Start menu entry behind an app id: does it exist, and which file does it start

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    /// <summary>Only the slot of GetString matters; the methods before it keep the layout.</summary>
    [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attribs);
        [PreserveSig] int Compare(IntPtr psi, uint hint, out int order);
        [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreWithCreateObject(int flags, IntPtr punk, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreForKeys(IntPtr keys, uint count, int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int Update(IntPtr pbc);
        [PreserveSig] int GetProperty(ref PROPERTYKEY key, IntPtr value);
        [PreserveSig] int GetCLSID(ref PROPERTYKEY key, out Guid clsid);
        [PreserveSig] int GetFileTime(ref PROPERTYKEY key, out long time);
        [PreserveSig] int GetInt32(ref PROPERTYKEY key, out int value);
        [PreserveSig] int GetString(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.LPWStr)] out string? value);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem2? item);

    // System.Link.TargetParsingPath
    private static PROPERTYKEY _linkTarget = new() { fmtid = new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), pid = 2 };

    private sealed record Entry(bool Exists, string? Target);
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    private static bool Usable(string appId) => appId.Length > 0 && appId.IndexOfAny(['"', '\r', '\n']) < 0;

    /// <summary>The app's entry in the Start menu. Call on the UI (STA) thread, like <see cref="ShellIcons"/>.</summary>
    private static Entry EntryOf(string appId)
    {
        if (Entries.TryGetValue(appId, out var hit)) return hit;
        var entry = new Entry(false, null);
        if (Usable(appId))
        {
            try
            {
                var iid = typeof(IShellItem2).GUID;
                if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + appId, IntPtr.Zero, ref iid, out var item) == 0 && item != null)
                {
                    string? target = item.GetString(ref _linkTarget, out var path) == 0 && !string.IsNullOrWhiteSpace(path) ? path : null;
                    entry = new Entry(true, target);
                    Marshal.ReleaseComObject(item);
                }
            }
            catch { /* no such entry */ }
        }
        return Entries[appId] = entry;
    }

    // ---- who owns a window

    private static (string Exe, string Package) ProcessOf(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return ("", "");
        try
        {
            var sb = new StringBuilder(1024);
            int n = sb.Capacity;
            string exe = QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString(0, n) : "";
            var id = new StringBuilder(256);
            int len = id.Capacity;
            string package = GetApplicationUserModelId(h, ref len, id) == 0 ? id.ToString() : "";
            return (exe, package);
        }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// How surely a program is the sender: 4 its Store app, 3 the file its Start menu entry starts,
    /// 2 named exactly like the app, 1 the name contains the app's, 0 not it.
    /// </summary>
    private static int Score(string exe, string package, string appId, string appName, Entry entry)
    {
        const StringComparison Any = StringComparison.OrdinalIgnoreCase;
        if (appId.Length > 0 && package.Equals(appId, Any)) return 4;
        if (exe.Length == 0) return 0;
        string file = Path.GetFileName(exe);
        if (entry.Target != null)
        {
            if (exe.Equals(entry.Target, Any)) return 3;
            string? home = Path.GetDirectoryName(entry.Target);
            if (string.IsNullOrEmpty(home) || !exe.StartsWith(home.TrimEnd('\\') + "\\", Any)) return 0;   // another program
            if (file.Equals(Path.GetFileName(entry.Target), Any)) return 3;
        }
        // a program without an entry of its own is named by its path ("{folder id}\Vendor\app.exe")
        else if (appId.EndsWith(".exe", Any) && file.Equals(appId[(appId.LastIndexOf('\\') + 1)..], Any)) return 3;
        var token = appName.Split(' ')[0];
        if (token.Length < 3) return 0;
        string name = Path.GetFileNameWithoutExtension(exe);
        return name.Equals(token, Any) ? 2 : name.Contains(token, Any) ? 1 : 0;
    }

    /// <summary>Is this window the sender's? (For "you switched to it yourself, so its messages are read".)</summary>
    public static bool Belongs(IntPtr window, string appId, string appName)
    {
        Native.GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0) return false;
        var (exe, package) = ProcessOf(pid);
        return Score(exe, package, appId, appName, EntryOf(appId)) > 0;
    }

    /// <summary>
    /// The sender's window: the best match, and among equals the one used last. Minimized windows count;
    /// a program hidden in the tray has none. <paramref name="how"/> says what it was found by, for the log.
    /// </summary>
    public static IntPtr Find(string appId, string appName, out string how)
    {
        var entry = EntryOf(appId);
        var seen = new Dictionary<uint, int>();
        IntPtr best = IntPtr.Zero;
        int bestScore = 0;
        string bestExe = "";
        uint self = (uint)Environment.ProcessId;

        EnumWindows((h, _) =>   // front to back
        {
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero || GetWindowTextLength(h) == 0) return true;
            if ((Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;   // a ghost of a suspended app
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0 || pid == self) return true;
            if (!seen.TryGetValue(pid, out int score))
            {
                var (exe, package) = ProcessOf(pid);
                seen[pid] = score = Score(exe, package, appId, appName, entry);
                if (score > bestScore) bestExe = Path.GetFileName(exe);
            }
            if (score > bestScore) { bestScore = score; best = h; }
            return bestScore < 4;   // nothing beats "it is that very app"
        }, IntPtr.Zero);

        how = bestScore switch { 4 => "app id", 3 => "program file", 2 or 1 => "name", _ => "no window" };
        if (best != IntPtr.Zero) how += " " + bestExe;
        return best;
    }

    /// <summary>Restore a window if it is minimized and put it in front.</summary>
    public static void Activate(IntPtr window)
    {
        if (IsIconic(window)) ShowWindow(window, SW_RESTORE);
        if (Native.SetForegroundWindow(window) && Native.GetForegroundWindow() == window) return;
        // Windows didn't let us through (another program holds the foreground): ask as part of its input queue
        uint me = GetCurrentThreadId();
        uint front = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        if (front == 0 || front == me) return;
        AttachThreadInput(me, front, true);
        BringWindowToTop(window);
        Native.SetForegroundWindow(window);
        AttachThreadInput(me, front, false);
    }

    /// <summary>
    /// Open the program a notification came from: its window comes forward; when it has none (it sits in the
    /// tray, or was closed) it is started the way the Start menu does, and a running one then shows itself.
    /// False when there is nothing to open (a notice from Windows itself).
    /// </summary>
    public static bool Open(string appId, string appName)
    {
        var window = Find(appId, appName, out _);
        if (window != IntPtr.Zero) { Activate(window); return true; }
        if (!EntryOf(appId).Exists) return false;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(@"shell:AppsFolder\" + appId) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception e) { Log.Write("open app: " + e.Message); return false; }
    }
}
