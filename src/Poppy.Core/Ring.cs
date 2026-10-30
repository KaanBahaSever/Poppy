namespace Poppy.Core;

/// <summary>
/// Geometry of the ring: segment 0 is centred at 12 o'clock, the rest follow clockwise.
/// Coordinates are relative to the ring centre, y pointing down (screen space).
/// </summary>
public sealed class RingLayout(int count, double outerRadius = 132, double innerRadius = 54)
{
    public const int Center = -1;
    public const int Outside = -2;

    public int Count { get; } = count;
    public double OuterRadius { get; } = outerRadius;
    public double InnerRadius { get; } = innerRadius;

    public double Sweep => Count == 0 ? 360 : 360.0 / Count;

    /// <summary>Start and end angle in degrees, 0 = 12 o'clock, clockwise.</summary>
    public (double Start, double End) Angles(int i) => (i * Sweep - Sweep / 2, i * Sweep + Sweep / 2);

    public int HitTest(double dx, double dy)
    {
        var r = Math.Sqrt(dx * dx + dy * dy);
        if (r < InnerRadius) return Center;
        // A little slack outside the ring so a fast flick still lands.
        if (r > OuterRadius + 24 || Count == 0) return Outside;
        var angle = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360 + Sweep / 2) % 360;
        return Math.Min(Count - 1, (int)(angle / Sweep));
    }

    /// <summary>Point in the middle of segment i (where its label goes).</summary>
    public (double X, double Y) SegmentCenter(int i)
    {
        var a = i * Sweep * Math.PI / 180;
        var r = (OuterRadius + InnerRadius) / 2;
        return (Math.Sin(a) * r, -Math.Cos(a) * r);
    }

    /// <summary>Point on the ring at angle (degrees, 0 = up, clockwise) and radius.</summary>
    public static (double X, double Y) Polar(double degrees, double radius)
    {
        var a = degrees * Math.PI / 180;
        return (Math.Sin(a) * radius, -Math.Cos(a) * radius);
    }
}

/// <summary>
/// Watches raw mouse and keyboard input and decides when to open the ring: a trigger key
/// tapped while the left button is held and the mouse has moved (a drag is in progress).
/// Pure logic, so it can be unit-tested without hooks.
/// </summary>
public sealed class DragGesture
{
    public int Threshold { get; set; } = 6;

    /// <summary>Maps a virtual-key code to a ring mode, or null if it is not a trigger.</summary>
    public Func<int, RingMode?> KeyToMode { get; set; } = _ => null;

    /// <summary>Extra check that a file drag (not a window move or text selection) is happening.</summary>
    public Func<bool> IsDragOfFiles { get; set; } = () => true;

    /// <summary>
    /// Whether trigger keys are hidden from other apps while the ring is open. Only safe when the
    /// drag is surely a file drag; otherwise Ctrl/Shift must still reach e.g. a text editor.
    /// </summary>
    public Func<bool> SwallowKeys { get; set; } = () => true;

    public event Action<RingMode, int, int>? Show;
    public event Action<RingMode>? Switch;
    public event Action? Released;
    public event Action? Cancelled;

    public bool ButtonDown { get; private set; }
    public bool Dragging { get; private set; }
    public RingMode? Open { get; private set; }

    int startX, startY, lastX, lastY;
    readonly HashSet<int> held = [];
    readonly HashSet<int> swallowed = [];

    public const int VK_ESCAPE = 0x1B;

    public void MouseDown(int x, int y)
    {
        ButtonDown = true;
        Dragging = false;
        startX = lastX = x;
        startY = lastY = y;
    }

    public void MouseMove(int x, int y)
    {
        lastX = x;
        lastY = y;
        if (ButtonDown && !Dragging && (Math.Abs(x - startX) >= Threshold || Math.Abs(y - startY) >= Threshold))
            Dragging = true;
    }

    public void MouseUp()
    {
        var wasOpen = Open != null;
        ButtonDown = false;
        Dragging = false;
        Open = null;
        if (wasOpen) Released?.Invoke();
    }

    /// <summary>The ring was closed from outside (drop handled, Esc in the ring...).</summary>
    public void Reset() => Open = null;

    /// <summary>Returns true when the key should be swallowed (not passed to other apps).</summary>
    public bool KeyDown(int vk)
    {
        if (!held.Add(vk)) return swallowed.Contains(vk); // auto-repeat
        if (vk == VK_ESCAPE && Open != null)
        {
            Open = null;
            Cancelled?.Invoke();
            return false; // let the drag source cancel too
        }
        var mode = KeyToMode(vk);
        if (mode == null || !ButtonDown || !Dragging) return false;
        if (Open == null)
        {
            if (!IsDragOfFiles()) return false;
            Open = mode;
            Show?.Invoke(mode.Value, lastX, lastY);
        }
        else if (Open != mode)
        {
            Open = mode;
            Switch?.Invoke(mode.Value);
        }
        // Swallowed so Explorer does not turn the drag into move/copy/link.
        if (!SwallowKeys()) return false;
        swallowed.Add(vk);
        return true;
    }

    public bool KeyUp(int vk)
    {
        held.Remove(vk);
        return swallowed.Remove(vk);
    }
}
