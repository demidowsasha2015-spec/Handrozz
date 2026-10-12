namespace UniMouse.Core.Runtime;

/// <summary>
/// Pure-logic helper for per-monitor DPI coordinate normalization.
/// Macro steps store absolute virtual-screen pixels captured at one DPI
/// setting; before playback we can map them into the current desktop bounds
/// so a macro recorded on a 4K@200% monitor still lands correctly when the
/// user later runs it on a 1080p@100% setup.
/// No Win32 calls here — callers pass in metrics they already have, which
/// keeps this class testable on any platform.
/// </summary>
public static class CoordinateSpace
{
    /// <summary>Screen metrics snapshot (physical pixels).</summary>
    public readonly record struct Bounds(int X, int Y, int Width, int Height)
    {
        /// <summary>Throws when width/height are not positive.</summary>
        public void Validate()
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Width, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Height, 0);
        }
    }

    /// <summary>
    /// Scales a point recorded under <paramref name="recordedOn"/> so it maps
    /// proportionally onto <paramref name="playbackOn"/> (stretch-to-fit).
    /// Result is clamped inside the playback bounds.
    /// </summary>
    public static (int X, int Y) MapPoint(
        int x, int y, Bounds recordedOn, Bounds playbackOn)
    {
        recordedOn.Validate();
        playbackOn.Validate();

        // Relative position within the recorded desktop (0..1), then stretch.
        double rx = (x - recordedOn.X) / (double)recordedOn.Width;
        double ry = (y - recordedOn.Y) / (double)recordedOn.Height;

        int nx = ClampTo(playbackOn.X + (int)Math.Round(rx * playbackOn.Width),
                         playbackOn.X, playbackOn.X + playbackOn.Width - 1);
        int ny = ClampTo(playbackOn.Y + (int)Math.Round(ry * playbackOn.Height),
                         playbackOn.Y, playbackOn.Y + playbackOn.Height - 1);
        return (nx, ny);
    }

    /// <summary>
    /// Adjusts a raw mouse-delta step (relative motion) for the resolution
    /// ratio between recording and playback screens, preserving direction.
    /// </summary>
    public static int ScaleDelta(int delta, Bounds recordedOn, Bounds playbackOn)
    {
        recordedOn.Validate();
        playbackOn.Validate();
        double ratio = playbackOn.Width / (double)recordedOn.Width;
        long scaled = (long)Math.Round(delta * ratio);
        return (int)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    private static int ClampTo(int value, int min, int max) =>
        value < min ? min : (value > max ? max : value);
}
