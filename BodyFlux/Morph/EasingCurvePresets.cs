using System.Numerics;

namespace BodyFlux.Morph;

/// <summary>
/// Named starting points offered in the curve editor's "Start from" picker. The first six match
/// CSS's built-in easing keywords (plus one overshoot example) so a curve tweaked from here feels
/// familiar to anyone who's used <c>cubic-bezier()</c> before; the rest are multi-point shapes that
/// simply could not be expressed with two control points: a settling bounce, a mid-morph pause, a
/// wind-up before the move, a staircase.
/// <para>
/// Every preset is stored as a <see cref="CurveKey"/> list, including the CSS six: for those,
/// <see cref="CurveKeys.FromControlPoints"/> gives the exactly equivalent two-anchor curve, so the
/// shapes are unchanged from when they were literal P1/P2 pairs. Order matters, index 0 is
/// "Linear", which callers use as the blank-slate default.
/// </para>
/// </summary>
public static class EasingCurvePresets
{
    /// <summary>
    /// One entry in <see cref="All"/>. <see cref="P1"/>/<see cref="P2"/> read the first and last
    /// segments' handles back out in the old two-control-point convention, which is all a caller
    /// that only speaks <see cref="CubicBezier.Evaluate"/> needs, and is exact for the two-anchor
    /// presets. For a multi-point preset they are a lossy summary of the shape, so a caller that
    /// wants the real curve reads <see cref="Keys"/> and checks <see cref="IsMultiPoint"/>.
    /// </summary>
    public readonly record struct Preset(string Name, CurveKey[] Keys)
    {
        public Vector2 P1 => Keys[0].OutPoint;
        public Vector2 P2 => Keys[^1].InPoint;

        /// <summary>True when the preset carries anchors beyond the two pinned endpoints.</summary>
        public bool IsMultiPoint => Keys.Length > 2;
    }

    public static readonly Preset[] All =
    [
        // ── CSS-equivalent two-point curves ───────────────────────────────────
        // Unchanged shapes, and unchanged order: index 0 must stay "Linear".
        new Preset("Linear",      CurveKeys.FromControlPoints(new Vector2(0f,    0f),    new Vector2(1f,    1f))),
        new Preset("Ease",        CurveKeys.FromControlPoints(new Vector2(0.25f, 0.1f),  new Vector2(0.25f, 1f))),
        new Preset("Ease In",     CurveKeys.FromControlPoints(new Vector2(0.42f, 0f),    new Vector2(1f,    1f))),
        new Preset("Ease Out",    CurveKeys.FromControlPoints(new Vector2(0f,    0f),    new Vector2(0.58f, 1f))),
        new Preset("Ease In-Out", CurveKeys.FromControlPoints(new Vector2(0.42f, 0f),    new Vector2(0.58f, 1f))),
        new Preset("Back",        CurveKeys.FromControlPoints(new Vector2(0.34f, 1.56f), new Vector2(0.64f, 1f))),

        // ── Multi-point curves ────────────────────────────────────────────────
        // The handles below are horizontal wherever the curve turns (a peak, a trough, either end
        // of a plateau), which is what makes those turns read as a settle or a stop rather than as
        // a corner. Sanitize leaves any in-bounds handle bit-identical, so these lists are exact
        // fixed points of it whatever their handles do; the tests pin that.

        // Bounce, overshoots the destination, falls back past it, and rings down in two smaller
        // swings before settling. Horizontal tangents at every peak and trough so each turn reads
        // as an impact rather than a corner, and the anchors crowd towards the end because a
        // settling bounce decays in time as well as in amplitude.
        new Preset("Bounce",
        [
            new CurveKey(new Vector2(0f,    0f),    Vector2.Zero,               new Vector2( 0.14f, 0.52f)),
            new CurveKey(new Vector2(0.40f, 1.15f), new Vector2(-0.13f, 0f),    new Vector2( 0.07f, 0f)),
            new CurveKey(new Vector2(0.60f, 0.93f), new Vector2(-0.07f, 0f),    new Vector2( 0.06f, 0f)),
            new CurveKey(new Vector2(0.78f, 1.06f), new Vector2(-0.06f, 0f),    new Vector2( 0.04f, 0f)),
            new CurveKey(new Vector2(0.90f, 0.98f), new Vector2(-0.04f, 0f),    new Vector2( 0.035f, 0f)),
            new CurveKey(new Vector2(1f,    1f),    new Vector2(-0.035f, 0f),   Vector2.Zero),
        ]),

        // Anticipate, winds up the other way first: dips below the starting profile, hangs there,
        // then swings through to a small overshoot and settles. The classic animation cheat that
        // makes a move read as deliberate instead of abrupt.
        new Preset("Anticipate",
        [
            new CurveKey(new Vector2(0f,    0f),     Vector2.Zero,              new Vector2( 0.10f, -0.05f)),
            new CurveKey(new Vector2(0.26f, -0.12f), new Vector2(-0.09f, 0f),   new Vector2( 0.22f,  0f)),
            new CurveKey(new Vector2(0.82f,  1.08f), new Vector2(-0.30f, 0f),   new Vector2( 0.07f,  0f)),
            new CurveKey(new Vector2(1f,     1f),    new Vector2(-0.09f, 0f),   Vector2.Zero),
        ]),

        // Hold, half the morph, then a dead stop across the middle, then the other half. The
        // plateau's two anchors share a y and both face it with horizontal handles, so the
        // segment between them is exactly flat, not merely slow: the morph visibly parks mid-way.
        new Preset("Hold",
        [
            new CurveKey(new Vector2(0f,    0f),   Vector2.Zero,               new Vector2( 0.15f, 0f)),
            new CurveKey(new Vector2(0.30f, 0.5f), new Vector2(-0.12f, 0f),    new Vector2( 0.14f, 0f)),
            new CurveKey(new Vector2(0.62f, 0.5f), new Vector2(-0.14f, 0f),    new Vector2( 0.15f, 0f)),
            new CurveKey(new Vector2(1f,    1f),   new Vector2(-0.16f, 0f),    Vector2.Zero),
        ]),

        // Steps, the same trick as Hold, twice over: three eased rises separated by two flat
        // treads, for a morph that arrives in visible stages.
        new Preset("Steps",
        [
            new CurveKey(new Vector2(0f,    0f),    Vector2.Zero,              new Vector2( 0.11f, 0f)),
            new CurveKey(new Vector2(0.22f, 0.33f), new Vector2(-0.09f, 0f),   new Vector2( 0.09f, 0f)),
            new CurveKey(new Vector2(0.40f, 0.33f), new Vector2(-0.09f, 0f),   new Vector2( 0.10f, 0f)),
            new CurveKey(new Vector2(0.60f, 0.66f), new Vector2(-0.10f, 0f),   new Vector2( 0.09f, 0f)),
            new CurveKey(new Vector2(0.78f, 0.66f), new Vector2(-0.09f, 0f),   new Vector2( 0.11f, 0f)),
            new CurveKey(new Vector2(1f,    1f),    new Vector2(-0.11f, 0f),   Vector2.Zero),
        ]),
    ];
}
