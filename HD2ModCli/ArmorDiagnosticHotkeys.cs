using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HD2ModCli;

/// <summary>Thread-level Windows hotkeys. No keyboard hook or game-process access.</summary>
public sealed class ArmorDiagnosticHotkeys : IDisposable
{
    private const int MarkerId = 0x4A71;
    private const int FinishId = 0x4A72;
    private const int CrashId = 0x4A73;
    private const uint WmHotkey = 0x312;
    private const uint PmRemove = 1;
    private const uint ModNoRepeat = 0x4000;
    private const uint F8 = 0x77;
    private const uint F9 = 0x78;
    private const uint F10 = 0x79;
    private bool markerRegistered, finishRegistered, crashRegistered;

    public ArmorDiagnosticHotkeys(bool includeCrash = false)
    {
        try
        {
            markerRegistered = RegisterHotKey(IntPtr.Zero, MarkerId, ModNoRepeat, F8);
            if (!markerRegistered) throw new Win32Exception(Marshal.GetLastWin32Error(), "F8 快捷键不可用。");
            finishRegistered = RegisterHotKey(IntPtr.Zero, FinishId, ModNoRepeat, F9);
            if (!finishRegistered) throw new Win32Exception(Marshal.GetLastWin32Error(), "F9 快捷键不可用。");
            if (includeCrash)
            {
                crashRegistered = RegisterHotKey(IntPtr.Zero, CrashId, ModNoRepeat, F10);
                if (!crashRegistered) throw new Win32Exception(Marshal.GetLastWin32Error(), "F10 快捷键不可用。");
            }
        }
        catch { Dispose(); throw; }
    }

    public HotkeyAction Poll()
    {
        while (PeekMessage(out var message, IntPtr.Zero, WmHotkey, WmHotkey, PmRemove))
        {
            if (message.WParam == (IntPtr)MarkerId) return HotkeyAction.Marker;
            if (message.WParam == (IntPtr)FinishId) return HotkeyAction.Finish;
            if (message.WParam == (IntPtr)CrashId) return HotkeyAction.Crash;
        }
        return HotkeyAction.None;
    }

    public static uint ForegroundPid()
    {
        _ = GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid;
    }

    public void Dispose()
    {
        if (crashRegistered) UnregisterHotKey(IntPtr.Zero, CrashId);
        if (finishRegistered) UnregisterHotKey(IntPtr.Zero, FinishId);
        if (markerRegistered) UnregisterHotKey(IntPtr.Zero, MarkerId);
        markerRegistered = finishRegistered = crashRegistered = false;
    }

    public enum HotkeyAction { None, Marker, Finish, Crash }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr HWnd;
        public uint Id;
        public IntPtr WParam, LParam;
        public uint Time;
        public Point Position;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}
