using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using static ScrollStrip.NativeMethods;

namespace ScrollStrip;

// Bounds and hit tests use physical screen coordinates, including on mixed-DPI monitors.
internal sealed record ScrollTarget(IntPtr HitWindow, Rect Bounds)
{
    private static int _probeBusy;

    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPhysicalPoint(POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    public bool ContainsCursor() =>
        GetPhysicalCursorPos(out var point) && Bounds.Contains(point.X, point.Y)
        && WindowFromPhysicalPoint(point) == HitWindow;

    public static Task<ScrollTarget?> Probe(bool vertical)
    {
        if (!GetPhysicalCursorPos(out var point)) return Task.FromResult<ScrollTarget?>(null);
        var hit = WindowFromPhysicalPoint(point);
        const uint gaRoot = 2;
        var root = GetAncestor(hit, gaRoot);
        var name = new StringBuilder(256);
        if (root == IntPtr.Zero || GetClassName(root, name, name.Capacity) == 0)
            return Task.FromResult<ScrollTarget?>(null);

        var rootClass = name.ToString();
        if (rootClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW")
            return Task.FromResult<ScrollTarget?>(null);

        // Third-party providers can stall. Never block raw input or queue more probes behind one.
        if (Interlocked.CompareExchange(ref _probeBusy, 1, 0) != 0)
            return Task.FromResult<ScrollTarget?>(null);

        long start = Stopwatch.GetTimestamp();
        return Task.Run(() =>
        {
            try
            {
                var target = Find(point, hit, IsBrowser(root, rootClass), vertical);
                return Stopwatch.GetElapsedTime(start).TotalMilliseconds <= 250 ? target : null;
            }
            catch (Exception ex) when (ex is SystemException)
            {
                // A disappearing window, protected process or unavailable provider is pointer-only.
                return null;
            }
            finally { Volatile.Write(ref _probeBusy, 0); }
        });
    }

    private static bool IsBrowser(IntPtr root, string rootClass)
    {
        if (rootClass == "MozillaWindowClass") return true;
        if (!rootClass.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal)) return false;
        GetWindowThreadProcessId(root, out int pid);
        using var process = Process.GetProcessById(pid);
        return process.ProcessName.ToLowerInvariant() is
            "chrome" or "msedge" or "brave" or "vivaldi" or "opera" or "chromium";
    }

    private static ScrollTarget? Find(POINT point, IntPtr hit, bool browser, bool vertical)
    {
        var position = new System.Windows.Point(point.X, point.Y);
        var element = AutomationElement.FromPoint(position);
        var walker = TreeWalker.RawViewWalker;
        var property = vertical ? ScrollPattern.VerticallyScrollableProperty
                                : ScrollPattern.HorizontallyScrollableProperty;
        Rect bounds = Rect.Empty;
        for (int depth = 0; element != null && depth < 64; depth++, element = walker.GetParent(element))
        {
            var type = element.Current.ControlType;
            // Toolbars and headers can themselves contain scrolling controls.
            if (type == ControlType.TitleBar || type == ControlType.ToolBar ||
                type == ControlType.MenuBar || (type == ControlType.TabItem && bounds.IsEmpty))
                return null;

            if (bounds.IsEmpty && element.GetCurrentPropertyValue(property, true) is true)
            {
                var candidate = element.Current.BoundingRectangle;
                if (candidate.Contains(position)) bounds = candidate;
            }

            // Chromium and Firefox also expose scrollable tab strips and other browser UI.
            // Only accept a scroll provider at or below the web document.
            if (type == ControlType.Document && !bounds.IsEmpty)
            {
                bounds.Intersect(element.Current.BoundingRectangle);
                return bounds.Contains(position) ? new ScrollTarget(hit, bounds) : null;
            }
            if (type == ControlType.Window) break;
        }
        if (browser) return null;
        if (!bounds.IsEmpty) return new ScrollTarget(hit, bounds);
        return NativeScrollBar.AtPoint(hit, position, vertical);
    }
}
