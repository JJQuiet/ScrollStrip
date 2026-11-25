using System.Runtime.InteropServices;
using static ScrollStrip.NativeMethods;

namespace ScrollStrip;

public record TouchContact(int ContactId, double NormX, double NormY, bool TipDown, double WidthToHeight = 1);

/// <summary>
/// Parses precision touchpad HID reports using preparsed data obtained
/// from GetRawInputDeviceInfo(RIDI_PREPARSEDDATA) — no CreateFile needed.
/// </summary>
public sealed class HidContactParser : IDisposable
{
    private const ushort UP_GENERIC   = 0x01;
    private const ushort UP_DIGITIZER = 0x0D;
    private const ushort U_X           = 0x30;
    private const ushort U_Y           = 0x31;
    private const ushort U_TIP         = 0x42;
    private const ushort U_CONTACT_ID  = 0x51;
    private const ushort U_CONTACT_COUNT = 0x54;

    private IntPtr _preparsedData;
    private HIDP_CAPS _caps;

    // link collection → (xMax, yMax); only collections with both are finger contacts
    private readonly Dictionary<ushort, (int xMax, int yMax, double width, double height)> _fingerCollections = new();
    // link collection → button usage (tip switch)
    private readonly HashSet<ushort> _tipCollections = new();

    public bool IsValid => _preparsedData != IntPtr.Zero;

    /// <summary>
    /// Initialize using the hDevice from RAWINPUTHEADER via RIDI_PREPARSEDDATA.
    /// This is the correct approach — no CreateFile required.
    /// </summary>
    public bool Init(IntPtr hDevice)
    {
        Free();

        uint size = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0) return false;

        _preparsedData = Marshal.AllocHGlobal((int)size);
        uint result = GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, _preparsedData, ref size);
        if (result == uint.MaxValue) { Free(); return false; }

        if (HidP_GetCaps(_preparsedData, out _caps) != HIDP_STATUS_SUCCESS)
        { Free(); return false; }

        BuildCollectionMaps();
        return _fingerCollections.Count > 0;
    }

    private void BuildCollectionMaps()
    {
        _fingerCollections.Clear();
        _tipCollections.Clear();

        // Value caps → find which link collections have X and Y
        ushort numValueCaps = _caps.NumberInputValueCaps;
        if (numValueCaps == 0) return;

        var valueCaps = new HIDP_VALUE_CAPS[numValueCaps];
        if (HidP_GetValueCaps(HIDP_REPORT_TYPE.HidP_Input, valueCaps, ref numValueCaps, _preparsedData)
            != HIDP_STATUS_SUCCESS) return;

        foreach (var vc in valueCaps)
        {
            if (vc.LinkCollection == 0) continue; // skip root collection
            if (!_fingerCollections.TryGetValue(vc.LinkCollection, out var lim))
                lim = default;

            if (vc.UsagePage == UP_GENERIC && vc.UsageMin == U_X)
                lim = (vc.LogicalMax, lim.yMax, AxisLength(vc), lim.height);
            else if (vc.UsagePage == UP_GENERIC && vc.UsageMin == U_Y)
                lim = (lim.xMax, vc.LogicalMax, lim.width, AxisLength(vc));

            _fingerCollections[vc.LinkCollection] = lim;
        }

        // Remove collections missing X or Y
        foreach (var key in _fingerCollections.Keys.Where(k => {
            var l = _fingerCollections[k];
            return l.xMax == 0 || l.yMax == 0;
        }).ToList())
            _fingerCollections.Remove(key);

        // Button caps → find which collections have Tip Switch
        ushort numButtonCaps = _caps.NumberInputButtonCaps;
        if (numButtonCaps == 0) return;

        var buttonCaps = new HIDP_BUTTON_CAPS[numButtonCaps];
        if (HidP_GetButtonCaps(HIDP_REPORT_TYPE.HidP_Input, buttonCaps, ref numButtonCaps, _preparsedData)
            != HIDP_STATUS_SUCCESS) return;

        foreach (var bc in buttonCaps)
        {
            if (bc.UsagePage == UP_DIGITIZER && bc.UsageMin == U_TIP)
                _tipCollections.Add(bc.LinkCollection);
        }
    }

    private static double AxisLength(HIDP_VALUE_CAPS cap)
    {
        double unit = cap.Units switch { 0x11 => 10.0, 0x13 => 25.4, _ => 0 };
        int exponent = (int)(cap.UnitsExp & 15);
        if (exponent >= 8) exponent -= 16;
        double logicalSpan = (double)cap.LogicalMax - cap.LogicalMin;
        return logicalSpan > 0 ? (cap.PhysicalMax - (double)cap.PhysicalMin) * unit
            * Math.Pow(10, exponent) * cap.LogicalMax / logicalSpan : 0;
    }

    public List<TouchContact> Parse(byte[] report)
    {
        if (!IsValid || report.Length == 0 || _fingerCollections.Count == 0) return [];

        var contacts = new List<TouchContact>();

        unsafe
        {
            fixed (byte* pReport = report)
            {
                IntPtr pRep = (IntPtr)pReport;
                uint repLen = (uint)report.Length;

                foreach (var (col, limits) in _fingerCollections)
                {
                    var xResult = HidP_GetUsageValue(HIDP_REPORT_TYPE.HidP_Input,
                        UP_GENERIC, col, U_X, out uint xVal, _preparsedData, pRep, repLen);
                    var yResult = HidP_GetUsageValue(HIDP_REPORT_TYPE.HidP_Input,
                        UP_GENERIC, col, U_Y, out uint yVal, _preparsedData, pRep, repLen);

                    if (xResult != HIDP_STATUS_SUCCESS || yResult != HIDP_STATUS_SUCCESS) continue;

                    HidP_GetUsageValue(HIDP_REPORT_TYPE.HidP_Input,
                        UP_DIGITIZER, col, U_CONTACT_ID, out uint contactId, _preparsedData, pRep, repLen);

                    // TipSwitch is a button — use HidP_GetUsages
                    bool tipDown = false;
                    if (_tipCollections.Contains(col))
                    {
                        var usages = new ushort[8];
                        uint usageLen = (uint)usages.Length;
                        int r = HidP_GetUsages(HIDP_REPORT_TYPE.HidP_Input,
                            UP_DIGITIZER, col, usages, ref usageLen, _preparsedData, pRep, repLen);
                        if (r == HIDP_STATUS_SUCCESS)
                            for (int i = 0; i < (int)usageLen; i++)
                                if (usages[i] == U_TIP) { tipDown = true; break; }
                    }
                    else
                    {
                        // Fallback: if X/Y are non-zero, assume finger is down
                        tipDown = xVal != 0 || yVal != 0;
                    }

                    if (!tipDown && xVal == 0 && yVal == 0) continue;

                    double normX = (double)xVal / limits.xMax;
                    double normY = (double)yVal / limits.yMax;

                    contacts.Add(new TouchContact((int)contactId, normX, normY, tipDown, limits.width > 0 && limits.height > 0 ? limits.width / limits.height : (double)limits.xMax / limits.yMax));
                }
            }
        }

        return contacts;
    }

    public int GetContactCount(byte[] report)
    {
        if (!IsValid || report.Length == 0) return 0;
        unsafe
        {
            fixed (byte* p = report)
            {
                int r = HidP_GetUsageValue(HIDP_REPORT_TYPE.HidP_Input,
                    UP_DIGITIZER, 0, U_CONTACT_COUNT, out uint count,
                    _preparsedData, (IntPtr)p, (uint)report.Length);
                return r == HIDP_STATUS_SUCCESS ? (int)count : 0;
            }
        }
    }

    private void Free()
    {
        if (_preparsedData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_preparsedData);
            _preparsedData = IntPtr.Zero;
        }
        _fingerCollections.Clear();
        _tipCollections.Clear();
    }

    public void Dispose() => Free();
}
