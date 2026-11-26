using System.Runtime.InteropServices;
using System.Text;
using static ScrollStrip.NativeMethods;

namespace ScrollStrip;

internal sealed record ScrollTarget(IntPtr Root, int TopHeight, int BottomHeight)
{
    private const uint RootAncestor = 2;
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPhysicalPoint(POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref POINT point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    public static ScrollTarget? AtCursor(int topHeight, int bottomHeight)
    {
        if (!GetPhysicalCursorPos(out var point)) return null;
        var root = GetAncestor(WindowFromPhysicalPoint(point), RootAncestor);
        var name = new StringBuilder(256);
        if (root == IntPtr.Zero || GetClassName(root, name, name.Capacity) == 0) return null;
        if (name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW")
            return null;
        var target = new ScrollTarget(root, topHeight, bottomHeight);
        return target.InContent(point) ? target : null;
    }

    public bool ContainsCursor() =>
        GetPhysicalCursorPos(out var point) &&
        GetAncestor(WindowFromPhysicalPoint(point), RootAncestor) == Root && InContent(point);

    private bool InContent(POINT point)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); // per-monitor aware v2
        if (previous == IntPtr.Zero) return false;
        try
        {
            if (!GetClientRect(Root, out var rect) || !ScreenToClient(Root, ref point)) return false;
            uint dpi = GetDpiForWindow(Root);
            if (dpi == 0) return false;
            int top = (int)Math.Round(TopHeight * dpi / 96.0);
            int bottom = (int)Math.Round(BottomHeight * dpi / 96.0);
            return point.X >= rect.Left && point.X < rect.Right &&
                point.Y >= rect.Top + top && point.Y < rect.Bottom - bottom;
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
}
