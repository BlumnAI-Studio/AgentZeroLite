using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using Agent.Common;
using Avalonia.Controls;
using Avalonia.Platform;

namespace AgentZeroAvalonia.Services.Plugins;

/// <summary>
/// "What the plugin's web view shows right now", as PNG in device pixels — the frame
/// <c>vision.analyze</c> / <c>vision.motion</c> crop. It has to come from the engine: the
/// plugin's YouTube player is a cross-origin iframe JS cannot read, and NativeWebView is a
/// native child surface Avalonia's own renderer does not draw.
///
/// <para>Implementations: <see cref="WebView2FrameCapture"/> on Windows. A macOS one
/// (WKWebView <c>takeSnapshot</c>, via <see cref="IAppleWKWebViewPlatformHandle"/>) is separate
/// work; until then <see cref="Create"/> answers null and the dispatcher reports
/// <c>capture-unavailable</c>.</para>
/// </summary>
internal interface IWebViewFrameCapture
{
    Task<byte[]?> CapturePngAsync(NativeWebView view);
}

internal static class WebViewFrameCapture
{
    /// <summary>The capture for this OS, or null where none is written yet.</summary>
    public static IWebViewFrameCapture? Create() =>
        OperatingSystem.IsWindows() ? new WebView2FrameCapture() : null;
}

/// <summary>
/// <c>ICoreWebView2::CapturePreview</c> — the call the WPF host makes through the WebView2 SDK,
/// made here through the raw COM pointer Avalonia hands out
/// (<see cref="IWindowsWebView2PlatformHandle.CoreWebView2"/>), because this host does not
/// reference the SDK. CapturePreview is slot 30 of the ICoreWebView2 vtable (3 IUnknown
/// slots, then the interface's methods in header order — Avalonia's own interop names it
/// <c>CapturePreview_30</c>). The image is written into an HGLOBAL-backed IStream and read
/// back once the completion handler fires. Must run on the UI thread, as every
/// CoreWebView2 call.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WebView2FrameCapture : IWebViewFrameCapture
{
    private const int CapturePreviewSlot = 30;
    private const int FormatPng = 0; // COREWEBVIEW2_CAPTURE_PREVIEW_IMAGE_FORMAT_PNG

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CapturePreviewFn(IntPtr self, int imageFormat, IntPtr imageStream, IntPtr handler);

    [ComImport, Guid("697e05e9-3d8f-45fa-96f4-8ffe1ededaf5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICoreWebView2CapturePreviewCompletedHandler
    {
        [PreserveSig] int Invoke(int errorCode);
    }

    [ComVisible(true)]
    private sealed class CompletedHandler(TaskCompletionSource<int> done) : ICoreWebView2CapturePreviewCompletedHandler
    {
        public int Invoke(int errorCode)
        {
            done.TrySetResult(errorCode);
            return 0;
        }
    }

    public async Task<byte[]?> CapturePngAsync(NativeWebView view)
    {
        if (view.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle handle || handle.CoreWebView2 == IntPtr.Zero)
            return null;

        IntPtr streamPtr = IntPtr.Zero, handlerPtr = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(IntPtr.Zero, true, out streamPtr));
            var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new CompletedHandler(done);
            handlerPtr = Marshal.GetComInterfaceForObject(handler, typeof(ICoreWebView2CapturePreviewCompletedHandler));

            var vtable = Marshal.ReadIntPtr(handle.CoreWebView2);
            var fn = Marshal.GetDelegateForFunctionPointer<CapturePreviewFn>(Marshal.ReadIntPtr(vtable, CapturePreviewSlot * IntPtr.Size));
            Marshal.ThrowExceptionForHR(fn(handle.CoreWebView2, FormatPng, streamPtr, handlerPtr));

            var hr = await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
            GC.KeepAlive(handler);
            if (hr < 0)
            {
                AppLogger.Log($"[Plugins] CapturePreview failed: 0x{hr:X8}");
                return null;
            }
            return ReadAll(streamPtr);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Plugins] frame capture failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            if (handlerPtr != IntPtr.Zero) Marshal.Release(handlerPtr);
            if (streamPtr != IntPtr.Zero) Marshal.Release(streamPtr);
        }
    }

    private static byte[] ReadAll(IntPtr streamPtr)
    {
        var stream = (IStream)Marshal.GetObjectForIUnknown(streamPtr);
        try
        {
            stream.Stat(out var stat, 1 /* STATFLAG_NONAME */);
            var length = checked((int)stat.cbSize);
            stream.Seek(0, 0 /* STREAM_SEEK_SET */, IntPtr.Zero);
            var bytes = new byte[length];
            var readPtr = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                stream.Read(bytes, length, readPtr);
                var read = Marshal.ReadInt32(readPtr);
                return read == length ? bytes : bytes[..read];
            }
            finally { Marshal.FreeHGlobal(readPtr); }
        }
        finally { Marshal.ReleaseComObject(stream); }
    }

    [DllImport("ole32.dll")]
    private static extern int CreateStreamOnHGlobal(IntPtr hGlobal, bool deleteOnRelease, out IntPtr stream);
}
