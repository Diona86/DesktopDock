internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public override string ToString() => $"[{Left},{Top},{Right},{Bottom})";
}

internal enum Edge { Top, Bottom, Left, Right }

internal sealed record HotZone(string Id, PixelRect Bounds, int EnterMs, int ExitMs)
{
    public static PixelRect Place(PixelRect screen, Edge edge, int start, int end, int thickness, int offset)
    {
        bool horizontal = edge is Edge.Top or Edge.Bottom;
        int edgeLength = horizontal ? screen.Width : screen.Height;
        int inwardLength = horizontal ? screen.Height : screen.Width;
        if (start < 0 || end <= start || end > edgeLength || thickness <= 0 || offset < 0 ||
            thickness > inwardLength || offset > inwardLength - thickness)
            throw new ArgumentException("Hot-zone bounds extend outside the monitor.");

        return edge switch
        {
            Edge.Top => new(screen.Left + start, screen.Top + offset, screen.Left + end, screen.Top + offset + thickness),
            Edge.Bottom => new(screen.Left + start, screen.Bottom - offset - thickness, screen.Left + end, screen.Bottom - offset),
            Edge.Left => new(screen.Left + offset, screen.Top + start, screen.Left + offset + thickness, screen.Top + end),
            Edge.Right => new(screen.Right - offset - thickness, screen.Top + start, screen.Right - offset, screen.Top + end),
            _ => throw new ArgumentOutOfRangeException(nameof(edge))
        };
    }
}

internal sealed class HotZoneTracker(HotZone[] zones, PixelRect[] interactionBounds)
{
    private HotZone? candidate;
    private long enteredAt;
    private HotZone? visible;
    private long? leftAt;

    public (string Action, string Zone, long ElapsedMs)? Sample(int x, int y, long now)
    {
        HotZone? hit = null;
        foreach (var zone in zones)
        {
            if (!zone.Bounds.Contains(x, y))
                continue;

            hit = zone;
            break;
        }

        if (visible is not null)
        {
            if (hit is not null)
            {
                leftAt = null;
                return null;
            }

            foreach (var bounds in interactionBounds)
            {
                if (bounds.Contains(x, y))
                {
                    leftAt = null;
                    return null;
                }
            }

            leftAt ??= now;
            long outsideMs = now - leftAt.Value;
            if (outsideMs < visible.ExitMs)
                return null;

            string id = visible.Id;
            visible = null;
            leftAt = null;
            return ("hide", id, outsideMs);
        }

        if (hit != candidate)
        {
            candidate = hit;
            enteredAt = now;
        }

        if (candidate is null || now - enteredAt < candidate.EnterMs)
            return null;

        visible = candidate;
        candidate = null;
        return ("show", visible.Id, now - enteredAt);
    }
}
