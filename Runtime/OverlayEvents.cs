namespace BIG.Unity.Overlay
{
    public enum OverlayCorner
    {
        BottomRight = 0,
        BottomLeft = 1,
        TopRight = 2,
        TopLeft = 3,
    }

    /// <summary> Raised whenever the overlay window lands in a corner (startup, UI, drag-snap). </summary>
    public readonly struct OverlayCornerChanged
    {
        public OverlayCornerChanged(OverlayCorner corner) => Corner = corner;
        public readonly OverlayCorner Corner;
    }

    /// <summary> Raised when the overlay window moves to another monitor. </summary>
    public readonly struct OverlayMonitorChanged
    {
        public OverlayMonitorChanged(int monitorIndex) => MonitorIndex = monitorIndex;
        public readonly int MonitorIndex;
    }

    /// <summary> Raised when the global cursor enters or leaves the overlay hit area. </summary>
    public readonly struct OverlayHoverChanged
    {
        public OverlayHoverChanged(bool isOver) => IsOver = isOver;
        public readonly bool IsOver;
    }
}
