using System.Runtime.InteropServices;
using Avalonia.Controls;
using Agent.Common;

namespace AgentZeroAvalonia.Services;

/// <summary>
/// "Look at me" from the OS chrome, the WPF host's <c>TaskbarFlasher</c> made portable: on
/// Windows the taskbar button flashes until the window comes to the front
/// (<c>FlashWindowEx</c>); on macOS the Dock icon bounces once
/// (<c>-[NSApplication requestUserAttention:]</c>, informational). Linux has no common
/// equivalent, so the popup is the signal there. Best-effort: a failure is logged, never thrown.
/// </summary>
public static class WindowAttention
{
    public static void Request(Window window)
    {
        try
        {
            if (OperatingSystem.IsWindows()) FlashWindows(window);
            else if (OperatingSystem.IsMacOS()) BounceDock();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Attention] window attention failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Windows ──────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_TRAY = 0x00000002;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    private static void FlashWindows(Window window)
    {
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        var fi = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
            uCount = 3,
        };
        FlashWindowEx(ref fi);
    }

    // ── macOS ────────────────────────────────────────────────────────────────

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, nint arg);

    /// <summary>NSInformationalRequest: one bounce, unlike NSCriticalRequest which bounces until the app is activated.</summary>
    private const nint NSInformationalRequest = 10;

    private static void BounceDock()
    {
        var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        if (app == IntPtr.Zero) return;
        Send(app, sel_registerName("requestUserAttention:"), NSInformationalRequest);
    }
}
