using System.Reflection;
using System.Runtime.InteropServices;

namespace DynamicIsland.Services;

/// <summary>
/// The short sound a message arrives with: Assets/sounds/notify1…4.wav (embedded), chosen in Settings → Behavior.
/// </summary>
public static class NotifySounds
{
    /// <summary>What Settings offers; an empty id means "no sound".</summary>
    public static readonly string[] Ids = ["1", "2", "3", "4"];

    private static readonly Dictionary<string, byte[]?> Loaded = [];
    private static long _playedAt;

    /// <summary>Play the sound with this id; nothing for "" or an unknown id.</summary>
    /// <param name="force">Play even right after another one (the preview in Settings).</param>
    public static void Play(string? id, bool force = false)
    {
        if (string.IsNullOrEmpty(id) || Array.IndexOf(Ids, id) < 0) return;
        long now = Environment.TickCount64;
        if (!force && now - _playedAt < 800) return;   // several messages at once: one sound, not a stutter
        _playedAt = now;
        try
        {
            if (Load(id) is { } wav)
                PlaySound(Marshal.UnsafeAddrOfPinnedArrayElement(wav, 0), IntPtr.Zero, SND_ASYNC | SND_NODEFAULT | SND_MEMORY);
        }
        catch { /* no audio device: stay silent */ }
    }

    private static byte[]? Load(string id)
    {
        if (Loaded.TryGetValue(id, out var known)) return known;
        byte[]? wav = null;
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"DynamicIsland.Assets.sounds.notify{id}.wav");
            if (s != null)
            {
                // Windows reads the sound after PlaySound returns, so the bytes must never move: a pinned array
                wav = GC.AllocateUninitializedArray<byte>((int)s.Length, pinned: true);
                s.ReadExactly(wav);
            }
        }
        catch (Exception e) { Log.Write("notify sound: " + e.Message); wav = null; }
        return Loaded[id] = wav;
    }

    private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
