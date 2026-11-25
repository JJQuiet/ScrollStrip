using System.Runtime.InteropServices;
using System.Windows;
using static ScrollStrip.NativeMethods;

namespace ScrollStrip;

internal static class NativeScrollBar
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo
    {
        public uint Size, Mask;
        public int Minimum, Maximum;
        public uint Page;
        public int Position, TrackPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct BarInfo
    {
        public uint Size;
        public RECT Bounds;
        public int ButtonSize, ThumbTop, ThumbBottom, Reserved;
        public fixed uint States[6];
    }

    [DllImport("user32.dll")] private static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref ScrollInfo info);
    [DllImport("user32.dll")] private static extern bool GetScrollBarInfo(IntPtr hwnd, int objectId, ref BarInfo info);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    // Consult only the hit HWND. Inheriting a frame's scrollbar would also activate its toolbar.
    public static unsafe ScrollTarget? AtPoint(IntPtr window, System.Windows.Point point, bool vertical)
    {
        const uint rangeAndPage = 3; // SIF_RANGE | SIF_PAGE
        var info = new ScrollInfo { Size = (uint)sizeof(ScrollInfo), Mask = rangeAndPage };
        if (!GetScrollInfo(window, vertical ? 1 : 0, ref info) ||
            (long)info.Maximum - info.Minimum < Math.Max(1L, (long)info.Page)) return null;

        var bar = new BarInfo { Size = (uint)sizeof(BarInfo) };
        const uint unavailableOrInvisible = 0x1 | 0x8000 | 0x10000;
        if (!GetScrollBarInfo(window, vertical ? -5 : -6, ref bar) ||
            (bar.States[0] & unavailableOrInvisible) != 0) return null;

        // UI Automation uses physical screen pixels. Match them when mapping the client rectangle.
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); // per-monitor aware v2
        if (previous == IntPtr.Zero) return null;
        try
        {
            if (!GetClientRect(window, out var client)) return null;
            var first = new POINT { X = client.Left, Y = client.Top };
            var last = new POINT { X = client.Right, Y = client.Bottom };
            if (!ClientToScreen(window, ref first) || !ClientToScreen(window, ref last)) return null;
            var bounds = new Rect(new System.Windows.Point(first.X, first.Y), new System.Windows.Point(last.X, last.Y));
            return bounds.Contains(point) ? new ScrollTarget(window, bounds) : null;
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
}
