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

    public static async Task<ScrollTarget?> Probe(bool vertical)
    {
        if (!GetPhysicalCursorPos(out var point)) return null;
        var hit = WindowFromPhysicalPoint(point);
        const uint gaRoot = 2;
        var root = GetAncestor(hit, gaRoot);
        var name = new StringBuilder(256);
        if (root == IntPtr.Zero || GetClassName(root, name, name.Capacity) == 0)
            return null;

        var rootClass = name.ToString();
        if (rootClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW")
            return null;

        var position = new System.Windows.Point(point.X, point.Y);
        var client = NativeScrollBar.ClientAtPoint(hit, position);
        if (client == null) return null; // Includes window borders and non-client title bars.

        // A stuck provider must not stall input or build an unbounded queue.
        if (Interlocked.CompareExchange(ref _probeBusy, 1, 0) != 0) return client;
        ScrollTarget? fallback = client;
        var lookup = Task.Run(() =>
        {
            try
            {
                var nativeScroll = NativeScrollBar.CanScroll(hit, vertical);
                if (nativeScroll == false) Volatile.Write(ref fallback, null);
                return Find(position, client, IsBrowser(root, rootClass), vertical, nativeScroll);
            }
            catch (Exception ex) when (ex is SystemException or ElementNotAvailableException)
            {
                return fallback;
            }
            finally { Volatile.Write(ref _probeBusy, 0); }
        });
        try { return await lookup.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false); }
        catch (TimeoutException) { return Volatile.Read(ref fallback); }
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

    private static ScrollTarget? Find(System.Windows.Point position, ScrollTarget client, bool browser, bool vertical, bool? nativeScroll)
    {
        var element = AutomationElement.FromPoint(position);
        var walker = TreeWalker.RawViewWalker;
        var property = vertical ? ScrollPattern.VerticallyScrollableProperty
                                : ScrollPattern.HorizontallyScrollableProperty;
        Rect bounds = Rect.Empty;
        Rect fallbackBounds = client.Bounds;
        bool reportsNoScroll = false, insideDocument = false, browserControl = false;
        for (int depth = 0; element != null && depth < 64; depth++, element = walker.GetParent(element))
        {
            var type = element.Current.ControlType;
            // Toolbars and headers can themselves contain scrolling controls.
            if (type == ControlType.TitleBar || type == ControlType.ToolBar ||
                type == ControlType.MenuBar || (type == ControlType.TabItem && bounds.IsEmpty))
                return null;

            if (depth == 0)
            {
                var leaf = element.Current.BoundingRectangle;
                if (leaf.Contains(position)) fallbackBounds.Intersect(leaf);
            }
            if (bounds.IsEmpty && element.GetCurrentPropertyValue(property, true) is bool canScroll)
            {
                if (canScroll)
                {
                    var candidate = element.Current.BoundingRectangle;
                    if (candidate.Contains(position)) bounds = candidate;
                }
                else if (element.GetCurrentPropertyValue(AutomationElement.IsScrollPatternAvailableProperty) is true)
                    reportsNoScroll = true;
            }

            if (type == ControlType.Document)
            {
                insideDocument = true;
                if (!bounds.IsEmpty)
                {
                    bounds.Intersect(element.Current.BoundingRectangle);
                    bounds.Intersect(client.Bounds);
                    return bounds.Contains(position) ? new ScrollTarget(client.HitWindow, bounds) : null;
                }
            }
            // Browser navigation controls have no Document ancestor. A fully opaque
            // client surface can be remote content, so it follows the unknown-area rule.
            if (type == ControlType.Edit || type == ControlType.Button || type == ControlType.Tab ||
                type == ControlType.MenuItem || type == ControlType.ComboBox)
                browserControl = true;
            if (type == ControlType.Window) break;
        }
        if (browser && !insideDocument && browserControl) return null;
        if (!bounds.IsEmpty && (!browser || insideDocument))
        {
            bounds.Intersect(client.Bounds);
            return bounds.Contains(position) ? new ScrollTarget(client.HitWindow, bounds) : null;
        }
        if (nativeScroll == true) return client;
        if (nativeScroll == false || reportsNoScroll) return null;
        return new ScrollTarget(client.HitWindow, fallbackBounds);
    }
}
