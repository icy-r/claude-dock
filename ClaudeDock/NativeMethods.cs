using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeDock;

internal static unsafe partial class NativeProcess
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(nint snapshot, PROCESSENTRY32W* entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(nint snapshot, PROCESSENTRY32W* entry);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    public static Dictionary<int, (int Parent, string Name)> Snapshot()
    {
        var map = new Dictionary<int, (int, string)>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == -1)
        {
            return map;
        }

        try
        {
            PROCESSENTRY32W e = default;
            e.dwSize = (uint)sizeof(PROCESSENTRY32W);
            if (Process32FirstW(snap, &e))
            {
                do
                {
                    map[(int)e.th32ProcessID] = ((int)e.th32ParentProcessID, new string(e.szExeFile));
                }
                while (Process32NextW(snap, &e));
            }
        }
        finally
        {
            CloseHandle(snap);
        }

        return map;
    }

    /// <summary>The pid itself followed by its ancestors.</summary>
    public static List<(int Pid, string Name)> GetAncestry(int pid)
    {
        var map = Snapshot();
        var result = new List<(int, string)>();
        var seen = new HashSet<int>();
        while (pid > 0 && seen.Add(pid) && map.TryGetValue(pid, out var e) && result.Count < 16)
        {
            result.Add((pid, e.Name));
            pid = e.Parent;
        }

        return result;
    }
}

internal static partial class NativeWindow
{
    private const int SW_RESTORE = 9;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowText(nint hwnd, [Out] char[] text, int max);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte vk, byte scan, uint flags, nuint extra);

    [LibraryImport("user32.dll", EntryPoint = "GetDoubleClickTime")]
    private static partial uint GetDoubleClickTime();

    public static int DoubleClickTimeMs() => (int)GetDoubleClickTime();

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassName(nint hwnd, [Out] char[] text, int max);

    public static string GetTitle(nint hwnd)
    {
        var buf = new char[512];
        var n = GetWindowText(hwnd, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : string.Empty;
    }

    public static string GetClass(nint hwnd)
    {
        var buf = new char[256];
        var n = GetClassName(hwnd, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : string.Empty;
    }

    /// <summary>Visible top-level windows with a title, with their owning pid.</summary>
    public static List<(nint Hwnd, int Pid, string Title)> TopLevelWindows()
    {
        var list = new List<(nint, int, string)>();
        EnumWindows(
            (h, _) =>
            {
                if (IsWindowVisible(h))
                {
                    var title = GetTitle(h);
                    if (title.Length > 0)
                    {
                        GetWindowThreadProcessId(h, out var pid);
                        list.Add((h, (int)pid, title));
                    }
                }

                return true;
            },
            0);
        return list;
    }

    public static bool BringToFront(nint hwnd)
    {
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }

        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd)
        {
            return true;
        }

        // Fallback: a synthetic Alt press lifts the foreground lock.
        keybd_event(VK_MENU, 0, 0, 0);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
        SetForegroundWindow(hwnd);
        return GetForegroundWindow() == hwnd;
    }
}
