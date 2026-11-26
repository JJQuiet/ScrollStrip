using System.Diagnostics;
using System.Runtime.InteropServices;
using static ScrollStrip.NativeMethods;

namespace ScrollStrip;

public sealed class EdgeScrollController : IDisposable
{
    private enum State { Idle, EdgeContact, Scrolling, Pointer }

    private readonly AppSettings _settings;
    private State _state = State.Idle;
    private int _trackedContactId = -1;
    private bool _wholePadGesture;
    private bool _directionConfirmed;
    private bool _cursorLocked;
    private ScrollTarget? _target;
    private ScrollTarget? _inertiaTarget;
    private double _lastNormY;
    private double _lastNormX;
    private double _topMotionAnchorX;
    private bool _isVertical;    // true = left, right or top edge (vertical), false = bottom edge (horizontal)
    private bool _usesHorizontalMotion; // input motion axis is independent of output wheel axis
    private double _accumulator;             // fractional scroll units carried between HID events
    private double _edgeContactDisplacement; // cumulative movement in EdgeContact; tap guard
    private const double ScrollActivationThreshold = 0.02; // 2% of touchpad — tap can't reach this

    // Inertia state — _velocity and _inertiaAccumulator are guarded by _inertiaLock.
    // _inertiaIsVertical is written before the timer starts (write-before-start guarantee).
    private readonly object _inertiaLock = new();
    private double _velocity;
    private double _inertiaAccumulator;
    private bool _inertiaIsVertical;
    private System.Timers.Timer? _inertiaTimer;
    private long _inertiaStartedAt; // Stopwatch.GetTimestamp() captured in BeginInertia

    // Velocity ring buffer — HID callback thread only, no lock needed.
    // Holds (timestamp, wheel-units) for the last 100 ms of gesture motion.
    private readonly Queue<(long Ts, double WheelUnits)> _velocityBuffer = new();
    private static readonly long VelocityWindowTicks = Stopwatch.Frequency / 10; // 100 ms

    private const double InertiaTickScale     = 2.0; // approx HID events per 16 ms timer tick
    private const double InertiaStopThreshold = 2.0; // |velocity| below which inertia stops

    // Strength 1–10 maps to per-tick decay factor:
    //   1 → 0.92  (~590 ms to 5% of launch velocity)
    //   5 → 0.951 (~1090 ms)
    //  10 → 0.99  (~4760 ms)
    private double InertiaDecay => 0.92 + (_settings.InertiaStrength - 1) / 9.0 * 0.07;

    // Milliseconds elapsed since inertia last started. HID callback thread only.
    private double InertiaGraceElapsedMs() =>
        (double)(Stopwatch.GetTimestamp() - _inertiaStartedAt) / Stopwatch.Frequency * 1000.0;

    public EdgeScrollController(AppSettings settings) => _settings = settings;

    public void OnContacts(List<TouchContact> contacts, int reportedCount)
    {
        if (!_settings.EdgeScrollEnabled)
        {
            if (_state != State.Idle) ExitScroll();
            StopInertia();
            return;
        }

        var activeContacts = contacts.Where(c => c.TipDown).ToList();
        int fingerCount = reportedCount > 0 ? reportedCount : activeContacts.Count;

        if (_state == State.Pointer && activeContacts.Any(c => c.ContactId == _trackedContactId)) return;

        if (fingerCount > 1)
        {
            if (_state != State.Idle) ExitScroll();
            // Touchpads emit phantom multi-touch reports within ~20 ms of a finger lifting.
            // Skip StopInertia for 50 ms so these artifacts don't cancel freshly-started inertia.
            if (InertiaGraceElapsedMs() >= 50) StopInertia();
            return;
        }

        if (activeContacts.Count == 0)
        {
            if (_state != State.Idle) ExitScroll();
            return;
        }

        var contact = activeContacts.FirstOrDefault();
        if (contact == default) { if (_state != State.Idle) ExitScroll(); return; }

        switch (_state)
        {
            case State.Idle:
                TryEnterEdge(contact);
                break;

            case State.EdgeContact:
            case State.Pointer:
            case State.Scrolling:
                if (contact.ContactId != _trackedContactId)
                {
                    ExitScroll();
                    TryEnterEdge(contact);
                    return;
                }
                if (_state != State.Pointer) ContinueScroll(contact);
                break;
        }
    }

    private void TryEnterEdge(TouchContact contact)
    {
        // Same phantom-touch guard: a spurious single-contact in the edge zone right after lift
        // would restart scrolling and cancel inertia — ignore it during the grace window.
        if (InertiaGraceElapsedMs() < 50) return;

        StopInertia();

        bool inLeftEdge = _settings.LeftEdgeEnabled && contact.NormX <= _settings.RightEdgeWidthPct / 100.0;
        bool inRightEdge  = _settings.RightEdgeEnabled  && contact.NormX >= 1.0 - _settings.RightEdgeWidthPct  / 100.0;
        bool inTopEdge = _settings.TopEdgeEnabled && contact.NormY <= _settings.TopEdgeHeightPct / 100.0;
        bool inBottomEdge = _settings.BottomEdgeEnabled && contact.NormY >= 1.0 - _settings.BottomEdgeHeightPct / 100.0;

        _wholePadGesture = _settings.WholePad;
        _trackedContactId = contact.ContactId;
        if (!_wholePadGesture && !inLeftEdge && !inRightEdge && !inTopEdge && !inBottomEdge)
        {
            _state = State.Pointer;
            return;
        }

        _isVertical       = _wholePadGesture || inLeftEdge || inRightEdge || inTopEdge; // prefer vertical scrolling at bottom corners
        // Top edge takes priority at top corners and maps X movement to the vertical wheel.
        _usesHorizontalMotion = !_wholePadGesture && (inTopEdge || !_isVertical);
        _target = ScrollTarget.AtCursor(_settings.PointerTopHeight, _settings.PointerBottomHeight);
        if (_target == null)
        {
            _state = State.Pointer;
            return;
        }
        _lastNormY        = contact.NormY;
        _lastNormX        = contact.NormX;
        _topMotionAnchorX = contact.NormX;
        _accumulator             = 0;
        _edgeContactDisplacement = 0;
        _velocityBuffer.Clear();
        _state            = State.EdgeContact;

        _directionConfirmed = false;
    }

    private void ContinueScroll(TouchContact contact)
    {
        // Until the first clear motion, the last coordinates are the touch-down point.
        if (!_directionConfirmed)
        {
            double dx = Math.Abs(contact.NormX - _lastNormX);
            double dy = Math.Abs(contact.NormY - _lastNormY);
            if (_wholePadGesture) dx *= contact.WidthToHeight;
            double along = _usesHorizontalMotion ? dx : dy;
            double across = _usesHorizontalMotion ? dy : dx;
            if (Math.Max(along, across) < (_wholePadGesture ? 0.01 : 0.002)) return;
            if (_wholePadGesture ? across > along * Math.Tan(_settings.AngleDegrees * Math.PI / 180) : along <= across * 1.2)
            {
                // Measure from touchdown: allow a small lateral start to turn into a vertical swipe.
                if (_wholePadGesture && Math.Max(along, across) < 0.04) return;
                _state = State.Pointer;
                return;
            }
            _directionConfirmed = true;
        }

        double delta = _usesHorizontalMotion
            ? _lastNormX - contact.NormX
            : _lastNormY - contact.NormY;

        _lastNormY = contact.NormY;
        _lastNormX = contact.NormX;

        if (_target == null || !_target.ContainsCursor())
        {
            if (_cursorLocked) UnlockCursor();
            _cursorLocked = false;
            _state = State.Pointer;
            return;
        }

        bool isTopGesture = _isVertical && _usesHorizontalMotion;
        if (isTopGesture)
        {
            // A positional dead band suppresses oscillating rest noise without losing slow swipes.
            // Follow only movement beyond the band, so crossing it produces no catch-up jump.
            double tolerance = _settings.TopJitterTolerancePct / 100.0;
            double displacement = _topMotionAnchorX - contact.NormX;
            if (Math.Abs(displacement) <= tolerance)
                delta = 0;
            else
            {
                double margin = Math.CopySign(tolerance, displacement);
                delta = displacement - margin;
                _topMotionAnchorX = contact.NormX + margin;
            }
        }
        double activationThreshold = isTopGesture
            ? _settings.TopActivationDistancePct / 100.0 : ScrollActivationThreshold;
        long now = Stopwatch.GetTimestamp();
        // Stationary reports must age out movement and lower the release velocity.
        if (delta == 0 || (!isTopGesture && Math.Abs(delta) < 0.0005))
        {
            if (_state == State.Scrolling)
            {
                _velocityBuffer.Enqueue((now, 0));
                TrimVelocityBuffer(now);
            }
            return;
        }

        if (_state == State.EdgeContact)
        {
            _edgeContactDisplacement += Math.Abs(delta);
            if (!_wholePadGesture && _edgeContactDisplacement < activationThreshold) return;
            if (_settings.LockCursorDuringScroll) _cursorLocked = LockCursor();
        }
        _state = State.Scrolling;

        double speed = !_isVertical ? _settings.HorizontalScrollSpeedMultiplier
            : _usesHorizontalMotion ? _settings.TopScrollSpeedMultiplier
            : _settings.VerticalScrollSpeedMultiplier;
        double scaled = delta * 2000.0 * speed;
        _accumulator += scaled;

        _velocityBuffer.Enqueue((now, scaled));
        TrimVelocityBuffer(now);

        int amount = (int)_accumulator;
        if (amount == 0) return;
        _accumulator -= amount;

        if (_isVertical  && _settings.InvertVerticalScroll)   amount = -amount;
        if (!_isVertical && _settings.InvertHorizontalScroll) amount = -amount;

        SendScrollEvent(amount, _isVertical);
    }

    private void TrimVelocityBuffer(long now)
    {
        while (_velocityBuffer.Count > 0 && now - _velocityBuffer.Peek().Ts > VelocityWindowTicks)
            _velocityBuffer.Dequeue();
    }

    private double CalculateLaunchVelocity(long now)
    {
        // Some touchpads send no reports while held still; also expire samples at lift.
        TrimVelocityBuffer(now);
        if (_velocityBuffer.Count == 0) return 0;
        double sum = 0;
        foreach (var (_, wheelUnits) in _velocityBuffer) sum += wheelUnits;
        return sum / _velocityBuffer.Count;
    }

    private void ExitScroll()
    {
        bool wasScrolling = _state == State.Scrolling;
        _state            = State.Idle;
        _trackedContactId = -1;
        _accumulator      = 0;
        if (_cursorLocked) UnlockCursor();
        _cursorLocked = false;

        double launchVelocity = CalculateLaunchVelocity(Stopwatch.GetTimestamp());

        if (wasScrolling && _target != null && _settings.InertiaEnabled && Math.Abs(launchVelocity) >= InertiaStopThreshold)
            BeginInertia(launchVelocity, _target);
        else
            lock (_inertiaLock) { _velocity = 0; _inertiaAccumulator = 0; }
        _target = null;
    }

    private void BeginInertia(double launchVelocity, ScrollTarget target)
    {
        _inertiaStartedAt = Stopwatch.GetTimestamp();
        lock (_inertiaLock)
        {
            _inertiaTimer?.Dispose();
            _velocity = launchVelocity;
            _inertiaAccumulator = 0;
            _inertiaIsVertical = _isVertical;
            _inertiaTarget = target;
            _inertiaTimer = new System.Timers.Timer(16) { AutoReset = true };
            _inertiaTimer.Elapsed += OnInertiaTimerElapsed;
            _inertiaTimer.Start();
        }
    }

    private void StopInertia()
    {
        lock (_inertiaLock)
        {
            _inertiaTimer?.Dispose();
            _inertiaTimer = null;
            _inertiaTarget = null;
            _velocity = 0;
            _inertiaAccumulator = 0;
        }
        _velocityBuffer.Clear();
    }

    private void OnInertiaTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        lock (_inertiaLock)
        {
            if (sender != _inertiaTimer || _inertiaTimer == null) return;
            _velocity *= InertiaDecay;
            if (Math.Abs(_velocity) < InertiaStopThreshold || _inertiaTarget?.ContainsCursor() != true)
            {
                _velocity = 0;
                _inertiaTimer.Dispose();
                _inertiaTimer = null;
                _inertiaTarget = null;
                return;
            }

            _inertiaAccumulator += _velocity * InertiaTickScale;
            int amount = (int)_inertiaAccumulator;
            if (amount == 0) return;
            _inertiaAccumulator -= amount;

            if (_inertiaIsVertical ? _settings.InvertVerticalScroll : _settings.InvertHorizontalScroll)
                amount = -amount;
            SendScrollEvent(amount, _inertiaIsVertical);
        }
    }

    private static void SendScrollEvent(int wheelAmount, bool vertical)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi   = new MOUSEINPUT
            {
                mouseData = (uint)wheelAmount,
                dwFlags   = vertical ? MOUSEEVENTF_WHEEL : MOUSEEVENTF_HWHEEL,
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static bool LockCursor()
    {
        if (!GetCursorPos(out var pt)) return false;
        var rect = new RECT { Left = pt.X, Top = pt.Y, Right = pt.X + 1, Bottom = pt.Y + 1 };
        return ClipCursor(ref rect);
    }

    private static void UnlockCursor() => ClipCursor(IntPtr.Zero);

    public void Dispose()
    {
        if (_cursorLocked) UnlockCursor();
        StopInertia();
    }
}
