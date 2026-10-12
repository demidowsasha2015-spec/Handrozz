using UniMouse.Core.Runtime;
using Xunit;

namespace UniMouse.Core.Tests;

public class CoordinateSpaceTests
{
    private static readonly CoordinateSpace.Bounds FullHd = new(0, 0, 1920, 1080);
    private static readonly CoordinateSpace.Bounds QuadHd = new(0, 0, 3840, 2160);

    [Fact]
    public void MapPoint_ScalesProportionally_WhenResolutionDoubles()
    {
        var (x, y) = CoordinateSpace.MapPoint(960, 540, FullHd, QuadHd);
        Assert.Equal((1920, 1080), (x, y));
    }

    [Fact]
    public void MapPoint_ShrinksBackToOriginal()
    {
        var (x, y) = CoordinateSpace.MapPoint(1920, 1080, QuadHd, FullHd);
        Assert.Equal((960, 540), (x, y));
    }

    [Fact]
    public void MapPoint_ClampsInsidePlaybackBounds()
    {
        // point beyond recorded width must clamp, not overflow
        var (x, y) = CoordinateSpace.MapPoint(5000, 5000, FullHd, QuadHd);
        Assert.InRange(x, 0, 3839);
        Assert.InRange(y, 0, 2159);
    }

    [Fact]
    public void MapPoint_HandlesOffsetVirtualDesktop()
    {
        // second monitor to the left of primary: recorded bounds start at -1920
        var recorded = new CoordinateSpace.Bounds(-1920, 0, 1920, 1080);
        var (x, _) = CoordinateSpace.MapPoint(-960, 0, recorded, FullHd);
        Assert.Equal(960, x);
    }

    [Fact]
    public void ScaleDelta_PreservesSignAndScalesMagnitude()
    {
        Assert.Equal(200, CoordinateSpace.ScaleDelta(100, FullHd, QuadHd));
        Assert.Equal(-200, CoordinateSpace.ScaleDelta(-100, FullHd, QuadHd));
    }

    [Fact]
    public void ScaleDelta_ClampsToShortRange()
    {
        Assert.Equal(short.MaxValue, CoordinateSpace.ScaleDelta(30000, FullHd, QuadHd));
    }

    [Fact]
    public void Bounds_Validate_RejectsNonPositiveSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CoordinateSpace.Bounds(0, 0, 0, 100).Validate());
    }
}
