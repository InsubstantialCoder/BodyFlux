using System.Linq;
using System.Numerics;
using BodyFlux.Morph;
using Xunit;

namespace BodyFlux.Tests.Morph;

public class CubicBezierTests
{
    private static Vector2 P1(string name) => EasingCurvePresets.All.First(p => p.Name == name).P1;
    private static Vector2 P2(string name) => EasingCurvePresets.All.First(p => p.Name == name).P2;

    [Fact]
    public void Evaluate_ClampsToBoundaries()
    {
        var p1 = P1("Ease");
        var p2 = P2("Ease");

        Assert.Equal(0f, CubicBezier.Evaluate(0f, p1, p2));
        Assert.Equal(1f, CubicBezier.Evaluate(1f, p1, p2));
    }

    [Fact]
    public void Evaluate_LinearPreset_IsIdentity()
    {
        // Linear's control points sit on the endpoints themselves (0,0)-(1,1), so the curve
        // degenerates to a straight line, eased(t) == t everywhere, same as EasingMode.Linear.
        var p1 = P1("Linear");
        var p2 = P2("Linear");

        // precision:3, not 4, Linear's control points give SolveForU a near-flat derivative close
        // to the edges (6u(1-u), which is 0 at u=0/1), so it sometimes falls through to the
        // bisection fallback, whose 12 fixed iterations resolve u to about 1/4096, not to
        // SubdivisionPrecision. That's still three orders of magnitude tighter than a visible glitch.
        for (float t = 0f; t <= 1f; t += 0.1f)
            Assert.Equal(t, CubicBezier.Evaluate(t, p1, p2), precision: 3);
    }

    [Theory]
    [InlineData("Ease")]
    [InlineData("Ease In")]
    [InlineData("Ease Out")]
    [InlineData("Ease In-Out")]
    public void Evaluate_IsMonotonicallyIncreasing_ForStandardCssPresets(string presetName)
    {
        // "Back" is deliberately excluded, it's an overshoot curve (P1.Y > 1) that dips past 1
        // and comes back by design, so it is NOT expected to be monotonic.
        var p1 = P1(presetName);
        var p2 = P2(presetName);

        float previous = 0f;
        for (float t = 0.05f; t <= 1f; t += 0.05f)
        {
            float value = CubicBezier.Evaluate(t, p1, p2);
            Assert.True(value >= previous - 1e-5f, $"{presetName} non-monotonic at t={t}: {value} < {previous}");
            previous = value;
        }
    }

    [Fact]
    public void Evaluate_SymmetricControlPoints_AreSymmetricAroundMidpoint()
    {
        // For any P1/P2 pair related by P2 = (1,1) - P1, the curve is point-symmetric about
        // (0.5, 0.5): eased(t) + eased(1-t) == 1 for every t. Ease In-Out's preset happens to have
        // this exact relationship (0.42,0)/(0.58,1) mirror through (0.5,0.5)).
        var p1 = P1("Ease In-Out");
        var p2 = P2("Ease In-Out");

        for (float t = 0.1f; t < 0.5f; t += 0.1f)
        {
            float a = CubicBezier.Evaluate(t, p1, p2);
            float b = CubicBezier.Evaluate(1f - t, p1, p2);
            Assert.Equal(1f, a + b, precision: 4);
        }
    }

    [Fact]
    public void Sample_ReturnsStepsPlusOnePoints_SpanningFullRange()
    {
        var points = CubicBezier.Sample(P1("Ease"), P2("Ease"), steps: 10);

        Assert.Equal(11, points.Length);
        Assert.Equal(0f, points[0].X, precision: 5);
        Assert.Equal(0f, points[0].Y, precision: 5);
        Assert.Equal(1f, points[^1].X, precision: 5);
        Assert.Equal(1f, points[^1].Y, precision: 5);
    }

    [Fact]
    public void Sample_XCoordinates_AreEvenlySpacedByStep()
    {
        // Sample's x is the raw step fraction i/steps, not the eased value. Only y comes from
        // CubicBezier.Evaluate. A caller drawing this as a polyline relies on that.
        var points = CubicBezier.Sample(P1("Ease In"), P2("Ease In"), steps: 4);

        Assert.Equal(new[] { 0f, 0.25f, 0.5f, 0.75f, 1f }, points.Select(p => p.X).ToArray());
    }
}
