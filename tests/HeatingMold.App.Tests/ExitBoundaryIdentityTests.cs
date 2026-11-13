using HeatingMold.App;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App.Tests;

public class ExitBoundaryIdentityTests
{
    [Fact]
    public void SameCadEdgeAndSelectedParameter_IsTheSameCandidate()
    {
        var binding = new ExitEdgeBinding(7, 3, 11, 42, 0.0, 1.0, 0.25, false);
        var a = new ExitBoundaryPoint(new Vec2(10, 20), -1, 0.25) { Binding = binding };
        var b = a;

        Assert.True(ExitBoundaryIdentity.Same(a, b));
    }

    [Fact]
    public void SameCoordinateAndNumericParameter_WithDifferentCadEdge_IsNotTheSameCandidate()
    {
        var a = new ExitBoundaryPoint(new Vec2(10, 20), -1, 0.25)
        {
            Binding = new ExitEdgeBinding(7, 3, 11, 42, 0.0, 1.0, 0.25, false)
        };
        var b = new ExitBoundaryPoint(new Vec2(10, 20), -1, 0.25)
        {
            Binding = new ExitEdgeBinding(7, 3, 11, 43, 0.0, 1.0, 0.25, false)
        };

        Assert.False(ExitBoundaryIdentity.Same(a, b));
    }

    [Fact]
    public void SameCoordinateButDifferentSelectedParameter_IsNotTheSameCandidate()
    {
        var a = new ExitBoundaryPoint(new Vec2(10, 20), -1, 0.25)
        {
            Binding = new ExitEdgeBinding(7, 3, 11, 42, 0.0, 1.0, 0.25, false)
        };
        var b = new ExitBoundaryPoint(new Vec2(10, 20), -1, 0.25)
        {
            Binding = new ExitEdgeBinding(7, 3, 11, 42, 0.0, 1.0, 0.50, false)
        };

        Assert.False(ExitBoundaryIdentity.Same(a, b));
    }

    [Fact]
    public void SameCoordinateButDifferentProvenanceKind_IsNotTheSameCandidate()
    {
        var cad = new ExitBoundaryPoint(new Vec2(10, 20), -1, 0.25)
        {
            Binding = new ExitEdgeBinding(7, 3, 11, 42, 0.0, 1.0, 0.25, false)
        };
        var split = new ExitBoundaryPoint(new Vec2(10, 20), -1, 0.25)
        {
            SplitBinding = new SplitLineBinding(3, 11, 0, 0, 100, 0, 0.0, 1.0, 0.25)
        };

        Assert.False(ExitBoundaryIdentity.Same(cad, split));
    }
}
