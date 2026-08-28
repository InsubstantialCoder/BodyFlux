using System;
using System.Collections.Generic;
using System.Numerics;

namespace BodyFlux.Morph;

/// <summary>
/// Evaluates cubic bezier easing curves, both the single-segment CSS <c>cubic-bezier()</c> shape
/// (<see cref="Evaluate"/>) and the multi-point piecewise form built from <see cref="CurveKey"/>
/// anchors (<see cref="EvaluateCurve"/>). Every segment of the latter is just one more cubic, so
/// both go through the same solver.
/// <para>
/// Because a bezier is parametric (x and y both vary with an internal parameter u, not with each
/// other directly), evaluating "y at a given x" requires solving for u first, which is WebKit's
/// <c>UnitBezier</c> approach: a few Newton-Raphson iterations, falling back to bisection when the
/// local slope is too flat for Newton to converge (near-vertical tangents from an extreme control
/// point).
/// </para>
/// </summary>
public static class CubicBezier
{
    private const int   NewtonIterations     = 4;
    private const float NewtonMinSlope       = 0.001f;
    private const float SubdivisionPrecision = 0.0000001f;
    private const int   SubdivisionMaxIterations = 12;

    /// <summary>
    /// Segments narrower than this on the x (time) axis are treated as an instant step to their end
    /// anchor instead of being solved: there is no meaningful u to find inside a zero-width domain,
    /// and the solver's slope test would be operating on noise.
    /// </summary>
    public const float MinSegmentSpan = 1e-5f;

    /// <summary>
    /// Returns the eased output for input progress <paramref name="t"/> (0..1 on the time axis) of
    /// the classic two-control-point curve from (0,0) to (1,1), the CSS
    /// <c>cubic-bezier(x1, y1, x2, y2)</c> shape, and the only shape BodyFlux curves had before
    /// multi-point support. Equivalent to a single <see cref="EvaluateSegment"/> across the unit
    /// square.
    /// </summary>
    public static float Evaluate(float t, Vector2 p1, Vector2 p2)
        => EvaluateSegment(t, Vector2.Zero, p1, p2, Vector2.One);

    /// <summary>
    /// Returns the y of one cubic segment at <paramref name="x"/>, for a segment with arbitrary
    /// endpoints. <paramref name="p0"/>/<paramref name="p3"/> are the anchors it runs between and
    /// <paramref name="c1"/>/<paramref name="c2"/> their handles, all in absolute curve space.
    /// Values outside [<paramref name="p0"/>.X, <paramref name="p3"/>.X] clamp to the nearest
    /// anchor, so a caller never has to range-check first.
    /// </summary>
    public static float EvaluateSegment(float x, Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3)
    {
        if (x <= p0.X) return p0.Y;
        if (x >= p3.X) return p3.Y;
        if (p3.X - p0.X <= MinSegmentSpan) return p3.Y;

        float u = SolveForU(x, p0.X, c1.X, c2.X, p3.X);
        return Component(u, p0.Y, c1.Y, c2.Y, p3.Y);
    }

    /// <summary>
    /// Returns the eased output at <paramref name="t"/> for a multi-point curve: locates the
    /// segment <paramref name="t"/> falls in and evaluates that one cubic. Outside the key range
    /// the first/last anchor's y is returned (for an editor-authored curve those anchors are pinned
    /// at (0,0) and (1,1), so this matches <see cref="Evaluate"/>'s 0/1 clamping).
    /// <para>
    /// Deliberately tolerant of a malformed key list, since a hand-edited config could hold
    /// unsorted or coincident anchors, and a morph rendering oddly beats one throwing or producing
    /// NaN scales on a character. A list too short to form a single segment degrades to linear, the same
    /// fallback <see cref="CurveKeys.Sanitize"/> repairs it to.
    /// </para>
    /// </summary>
    public static float EvaluateCurve(float t, IReadOnlyList<CurveKey> keys)
    {
        if (keys is null || keys.Count < 2) return t;

        var first = keys[0];
        var last  = keys[keys.Count - 1];
        if (t <= first.Pos.X) return first.Pos.Y;
        if (t >= last.Pos.X)  return last.Pos.Y;

        // Last anchor at or before t. A linear scan beats a binary search at the handful of keys
        // the editor allows (see CurveKeys.MaxKeys) and stays correct for a single-segment curve.
        int seg = 0;
        for (int i = 0; i < keys.Count - 1; i++)
            if (t >= keys[i].Pos.X) seg = i;

        var a = keys[seg];
        var b = keys[seg + 1];
        return EvaluateSegment(t, a.Pos, a.OutPoint, b.InPoint, b.Pos);
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

    /// <summary>
    /// (t, easedT) points along a multi-point curve, the <see cref="EvaluateCurve"/> counterpart of
    /// <see cref="Sample"/>. x stays the evenly spaced step fraction, so anchors do not land exactly
    /// on a sample; a caller that wants the anchors marked draws them from the key list itself.
    /// </summary>
    public static Vector2[] SampleCurve(IReadOnlyList<CurveKey> keys, int steps)
    {
        var points = new Vector2[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            points[i] = new Vector2(t, EvaluateCurve(t, keys));
        }
        return points;
    }

    /// <summary>The two sub-segments a <see cref="SplitSegment"/> produced, in absolute curve space:
    /// left is (p0, <see cref="LeftC1"/>, <see cref="LeftC2"/>, <see cref="Mid"/>), right is
    /// (<see cref="Mid"/>, <see cref="RightC1"/>, <see cref="RightC2"/>, p3).</summary>
    public readonly record struct SegmentSplit(
        Vector2 LeftC1, Vector2 LeftC2, Vector2 Mid, Vector2 RightC1, Vector2 RightC2);

    /// <summary>
    /// Splits one cubic segment at parameter <paramref name="u"/> into two that together trace the
    /// exact same shape (de Casteljau subdivision). This is what lets the editor insert an anchor
    /// into an existing curve without the curve visibly moving.
    /// </summary>
    public static SegmentSplit SplitSegment(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3, float u)
    {
        u = Math.Clamp(u, 0f, 1f);

        var a = Vector2.Lerp(p0, c1, u);
        var b = Vector2.Lerp(c1, c2, u);
        var c = Vector2.Lerp(c2, p3, u);
        var d = Vector2.Lerp(a,  b,  u);
        var e = Vector2.Lerp(b,  c,  u);
        var m = Vector2.Lerp(d,  e,  u);

        return new SegmentSplit(a, d, m, e, c);
    }

    /// <summary>
    /// The internal parameter u at which a segment reaches <paramref name="x"/>, the value
    /// <see cref="SplitSegment"/> needs to cut a segment at a given point on the time axis.
    /// </summary>
    public static float ParameterAt(float x, Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3)
    {
        if (p3.X - p0.X <= MinSegmentSpan) return 0f;
        return Math.Clamp(SolveForU(x, p0.X, c1.X, c2.X, p3.X), 0f, 1f);
    }

    // Cubic bezier component on one axis, with both endpoints explicit:
    // B(u) = (1-u)^3 p0 + 3(1-u)^2 u c1 + 3(1-u) u^2 c2 + u^3 p3.
    private static float Component(float u, float p0, float c1, float c2, float p3)
    {
        float v = 1f - u;
        return v * v * v * p0 + 3f * v * v * u * c1 + 3f * v * u * u * c2 + u * u * u * p3;
    }

    private static float Derivative(float u, float p0, float c1, float c2, float p3)
    {
        float v = 1f - u;
        return 3f * v * v * (c1 - p0) + 6f * v * u * (c2 - c1) + 3f * u * u * (p3 - c2);
    }

    private static float SolveForU(float x, float p0x, float c1x, float c2x, float p3x)
    {
        float span  = p3x - p0x;
        float guess = span > MinSegmentSpan ? (x - p0x) / span : 0.5f;
        float u     = guess; // the normalised position within the segment is a decent first guess

        for (int i = 0; i < NewtonIterations; i++)
        {
            float xEst = Component(u, p0x, c1x, c2x, p3x) - x;
            // NewtonMinSlope is an absolute threshold tuned for a full-width segment; a narrow
            // segment's dx/du scales down with its span and so bails out to bisection sooner, which
            // is the safe direction to be wrong in.
            float d = Derivative(u, p0x, c1x, c2x, p3x);
            if (MathF.Abs(d) < NewtonMinSlope) break;
            u -= xEst / d;
        }

        if (u >= 0f && u <= 1f && MathF.Abs(Component(u, p0x, c1x, c2x, p3x) - x) < SubdivisionPrecision)
            return u;

        float lo = 0f, hi = 1f;
        u = guess;
        for (int i = 0; i < SubdivisionMaxIterations; i++)
        {
            float xEst = Component(u, p0x, c1x, c2x, p3x) - x;
            if (MathF.Abs(xEst) < SubdivisionPrecision) break;
            if (xEst > 0f) hi = u; else lo = u;
            u = (lo + hi) / 2f;
        }
        return u;
    }
}
