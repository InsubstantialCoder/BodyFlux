using System;
using System.Numerics;

namespace BodyFlux.Morph;

/// <summary>
/// Evaluates a cubic bezier easing curve from (0,0) to (1,1) through two interior control points
/// (<see cref="EasingCurve.P1"/>/<see cref="EasingCurve.P2"/>), the same shape CSS's
/// <c>cubic-bezier()</c> uses. Because the curve is parametric (x and y both vary with an internal
/// parameter u, not with each other directly), evaluating "y at a given x" requires solving for u
/// first — this is WebKit's <c>UnitBezier</c> approach: a few Newton-Raphson iterations, falling back
/// to bisection when the local slope is too flat for Newton to converge (near-vertical tangents from
/// an extreme control point).
/// </summary>
public static class CubicBezier
{
    private const int   NewtonIterations     = 4;
    private const float NewtonMinSlope       = 0.001f;
    private const float SubdivisionPrecision = 0.0000001f;
    private const int   SubdivisionMaxIterations = 12;

    /// <summary>Returns the eased output for input progress <paramref name="t"/> (0..1 on the time axis).</summary>
    public static float Evaluate(float t, Vector2 p1, Vector2 p2)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        float u = SolveForU(t, p1.X, p2.X);
        return Component(u, p1.Y, p2.Y);
    }

    /// <summary>(t, easedT) points along the curve, for drawing a preview polyline.</summary>
    public static Vector2[] Sample(Vector2 p1, Vector2 p2, int steps)
    {
        var points = new Vector2[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            points[i] = new Vector2(t, Evaluate(t, p1, p2));
        }
        return points;
    }

    // Cubic bezier component with fixed endpoints 0 and 1: B(u) = 3(1-u)^2 u c1 + 3(1-u)u^2 c2 + u^3.
    private static float Component(float u, float c1, float c2)
    {
        float v = 1f - u;
        return 3f * v * v * u * c1 + 3f * v * u * u * c2 + u * u * u;
    }

    private static float Derivative(float u, float c1, float c2)
    {
        float v = 1f - u;
        return 3f * v * v * c1 + 6f * v * u * (c2 - c1) + 3f * u * u * (1f - c2);
    }

    private static float SolveForU(float x, float c1x, float c2x)
    {
        float u = x; // x is a decent initial guess for u on most easing shapes

        for (int i = 0; i < NewtonIterations; i++)
        {
            float xEst = Component(u, c1x, c2x) - x;
            float d    = Derivative(u, c1x, c2x);
            if (MathF.Abs(d) < NewtonMinSlope) break;
            u -= xEst / d;
        }

        if (u >= 0f && u <= 1f && MathF.Abs(Component(u, c1x, c2x) - x) < SubdivisionPrecision)
            return u;

        float lo = 0f, hi = 1f;
        u = x;
        for (int i = 0; i < SubdivisionMaxIterations; i++)
        {
            float xEst = Component(u, c1x, c2x) - x;
            if (MathF.Abs(xEst) < SubdivisionPrecision) break;
            if (xEst > 0f) hi = u; else lo = u;
            u = (lo + hi) / 2f;
        }
        return u;
    }
}
