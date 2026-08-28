using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BodyFlux.Morph;
using Xunit;

namespace BodyFlux.Tests.Morph;

/// <summary>
/// Multi-point easing curves: the piecewise evaluation, the key-list invariants in
/// <see cref="CurveKeys"/>, and the two properties the feature rests on: that a curve saved before
/// multi-point support evaluates exactly as it always did, and that inserting an anchor does not
/// move the curve.
/// </summary>
public class MultiPointCurveTests
{
    // A hold shape: rise, plateau at y=0.25 across the middle, rise again. Impossible to express
    // with a single cubic, so it doubles as proof the piecewise path is really being used.
    private static List<CurveKey> HoldCurve() => CurveKeys.Sanitize(
    [
        new CurveKey(new Vector2(0f,   0f),    Vector2.Zero,              new Vector2(0.1f, 0f)),
        new CurveKey(new Vector2(0.3f, 0.25f), new Vector2(-0.1f, 0f),    new Vector2(0.1f, 0f)),
        new CurveKey(new Vector2(0.7f, 0.25f), new Vector2(-0.1f, 0f),    new Vector2(0.1f, 0f)),
        new CurveKey(new Vector2(1f,   1f),    new Vector2(-0.1f, 0f),    Vector2.Zero),
    ]);

    // Two evaluations of the same shape differ by the solver's residual on u amplified by the
    // local slope, not by decimal places, so shape comparisons assert against this instead of a
    // rounded equality (which fails on a rounding boundary for a difference this small).
    private const float ShapeTolerance = 1e-3f;

    private static (Vector2 P1, Vector2 P2) Preset(string name)
    {
        var p = EasingCurvePresets.All.First(x => x.Name == name);
        return (p.P1, p.P2);
    }

    // ── Legacy compatibility ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Linear")]
    [InlineData("Ease")]
    [InlineData("Ease In")]
    [InlineData("Ease Out")]
    [InlineData("Ease In-Out")]
    [InlineData("Back")]
    public void Resolve_LegacyCurve_EvaluatesIdenticallyToTwoPointForm(string presetName)
    {
        // The whole no-migration plan rests on this: a curve stored as P1/P2 (every curve saved
        // before multi-point support) projected onto two anchors is the SAME curve, not an
        // approximation of it.
        var (p1, p2) = Preset(presetName);
        var curve    = new EasingCurve(Guid.NewGuid(), presetName, p1, p2);
        var keys     = curve.Resolve();

        Assert.Equal(2, keys.Length);
        for (float t = 0f; t <= 1f; t += 0.02f)
            Assert.Equal(CubicBezier.Evaluate(t, p1, p2), CubicBezier.EvaluateCurve(t, keys), precision: 5);
    }

    [Fact]
    public void Resolve_MultiPointCurve_ReturnsACopy_SoAnEditCannotReshapeARunningMorph()
    {
        var stored = HoldCurve();
        var curve  = new EasingCurve(Guid.NewGuid(), "Hold", Vector2.Zero, Vector2.One, stored);

        var resolved = curve.Resolve();
        stored[1] = stored[1] with { Pos = new Vector2(0.3f, 0.9f) };

        Assert.Equal(0.25f, resolved[1].Pos.Y, precision: 5);
    }

    [Fact]
    public void FromKeys_FillsLegacyControlPoints_ForOlderPluginVersions()
    {
        var keys  = HoldCurve();
        var curve = EasingCurve.FromKeys(Guid.NewGuid(), "Hold", keys);

        Assert.True(curve.IsMultiPoint);
        Assert.Equal(4, curve.PointCount);
        // First segment's outgoing handle and last segment's incoming handle, in absolute space.
        Assert.Equal(0.1f, curve.P1.X, precision: 5);
        Assert.Equal(0f,   curve.P1.Y, precision: 5);
        Assert.Equal(0.9f, curve.P2.X, precision: 5);
        Assert.Equal(1f,   curve.P2.Y, precision: 5);
    }

    // ── Evaluation ───────────────────────────────────────────────────────────

    [Fact]
    public void EvaluateCurve_PassesThroughEveryAnchor()
    {
        var keys = HoldCurve();
        foreach (var key in keys)
            Assert.Equal(key.Pos.Y, CubicBezier.EvaluateCurve(key.Pos.X, keys), precision: 4);
    }

    [Fact]
    public void EvaluateCurve_IsContinuousAcrossSegmentBoundaries()
    {
        // Approaching a knot from the left must land on the same value the anchor holds. A gap
        // here would show up as a visible jolt mid-morph.
        var keys = HoldCurve();
        foreach (var key in keys.Skip(1))
        {
            float justBefore = CubicBezier.EvaluateCurve(key.Pos.X - 0.001f, keys);
            Assert.Equal(key.Pos.Y, justBefore, precision: 2);
        }
    }

    [Fact]
    public void EvaluateCurve_ClampsOutsideTheKeyRange()
    {
        var keys = HoldCurve();

        Assert.Equal(0f, CubicBezier.EvaluateCurve(-0.5f, keys), precision: 5);
        Assert.Equal(0f, CubicBezier.EvaluateCurve(0f,    keys), precision: 5);
        Assert.Equal(1f, CubicBezier.EvaluateCurve(1f,    keys), precision: 5);
        Assert.Equal(1f, CubicBezier.EvaluateCurve(1.5f,  keys), precision: 5);
    }

    [Fact]
    public void EvaluateCurve_HoldSegment_StaysFlatAcrossThePlateau()
    {
        var keys = HoldCurve();
        for (float t = 0.32f; t <= 0.68f; t += 0.04f)
            Assert.Equal(0.25f, CubicBezier.EvaluateCurve(t, keys), precision: 3);
    }

    [Fact]
    public void EvaluateCurve_IsMonotonic_ForAMonotonicKeyList()
    {
        var   keys     = HoldCurve();
        float previous = 0f;
        for (float t = 0f; t <= 1f; t += 0.01f)
        {
            float value = CubicBezier.EvaluateCurve(t, keys);
            Assert.True(value >= previous - 1e-4f, $"non-monotonic at t={t}: {value} < {previous}");
            previous = value;
        }
    }

    [Fact]
    public void EvaluateCurve_CoincidentAnchors_StepInsteadOfProducingNaN()
    {
        // Not reachable through the editor (MinGap forbids it), but a hand-edited config can hold
        // it, and a NaN here would reach a character's bone scales.
        var keys = new List<CurveKey>
        {
            new(new Vector2(0f,   0f),   Vector2.Zero, Vector2.Zero),
            new(new Vector2(0.5f, 0.2f), Vector2.Zero, Vector2.Zero),
            new(new Vector2(0.5f, 0.8f), Vector2.Zero, Vector2.Zero),
            new(new Vector2(1f,   1f),   Vector2.Zero, Vector2.Zero),
        };

        for (float t = 0f; t <= 1f; t += 0.05f)
            Assert.False(float.IsNaN(CubicBezier.EvaluateCurve(t, keys)), $"NaN at t={t}");

        Assert.True(CubicBezier.EvaluateCurve(0.49f, keys) < 0.25f);
        Assert.Equal(0.8f, CubicBezier.EvaluateCurve(0.5f, keys), precision: 4);
    }

    [Fact]
    public void EvaluateCurve_DegenerateInput_FallsBackToIdentity()
    {
        Assert.Equal(0.4f, CubicBezier.EvaluateCurve(0.4f, []), precision: 5);
        Assert.Equal(0.7f, CubicBezier.EvaluateCurve(0.7f, new List<CurveKey>
        {
            new(new Vector2(0f, 0.5f), Vector2.Zero, Vector2.Zero),
        }), precision: 5);
    }

    [Fact]
    public void SampleCurve_ReturnsStepsPlusOnePoints_SpanningTheFullRange()
    {
        var points = CubicBezier.SampleCurve(HoldCurve(), steps: 10);

        Assert.Equal(11, points.Length);
        Assert.Equal(new[] { 0f, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1f },
                     points.Select(p => MathF.Round(p.X, 4)).ToArray());
        Assert.Equal(0f, points[0].Y,  precision: 5);
        Assert.Equal(1f, points[^1].Y, precision: 5);
    }

    // ── Split / insert ───────────────────────────────────────────────────────

    [Fact]
    public void SplitSegment_ProducesTwoHalvesTracingTheOriginalShape()
    {
        var p0 = new Vector2(0f, 0f);
        var c1 = new Vector2(0.25f, 0.9f);
        var c2 = new Vector2(0.6f, 0.1f);
        var p3 = new Vector2(1f, 1f);

        var split = CubicBezier.SplitSegment(p0, c1, c2, p3, 0.4f);

        for (float x = 0.01f; x < 1f; x += 0.01f)
        {
            float whole = CubicBezier.EvaluateSegment(x, p0, c1, c2, p3);
            float piece = x <= split.Mid.X
                ? CubicBezier.EvaluateSegment(x, p0, split.LeftC1, split.LeftC2, split.Mid)
                : CubicBezier.EvaluateSegment(x, split.Mid, split.RightC1, split.RightC2, p3);
            Assert.True(MathF.Abs(whole - piece) < ShapeTolerance,
                        $"split deviates at x={x}: {whole} vs {piece}");
        }
    }

    [Fact]
    public void Insert_AddsAnAnchorOnTheCurveWithoutChangingItsShape()
    {
        // The reason insertion uses a de Casteljau split rather than just dropping a point in:
        // adding a control point should feel like nothing happened until you drag it.
        var (p1, p2) = Preset("Ease");
        var before   = CurveKeys.FromControlPoints(p1, p2);
        var after    = CurveKeys.Insert(before, 0.4f);

        Assert.NotNull(after);
        Assert.Equal(3, after!.Count);
        Assert.Equal(0.4f, after[1].Pos.X, precision: 3);
        Assert.Equal(CubicBezier.EvaluateCurve(0.4f, before), after[1].Pos.Y, precision: 3);

        for (float t = 0f; t <= 1f; t += 0.01f)
        {
            float shifted = MathF.Abs(CubicBezier.EvaluateCurve(t, before) - CubicBezier.EvaluateCurve(t, after));
            Assert.True(shifted < ShapeTolerance, $"insert moved the curve at t={t} by {shifted}");
        }
    }

    [Fact]
    public void Insert_PreservesShape_OnAnOvershootCurveToo()
    {
        var (p1, p2) = Preset("Back");
        var before   = CurveKeys.FromControlPoints(p1, p2);
        var after    = CurveKeys.Insert(before, 0.7f);

        Assert.NotNull(after);
        for (float t = 0f; t <= 1f; t += 0.01f)
        {
            float shifted = MathF.Abs(CubicBezier.EvaluateCurve(t, before) - CubicBezier.EvaluateCurve(t, after!));
            Assert.True(shifted < ShapeTolerance, $"insert moved the curve at t={t} by {shifted}");
        }
    }

    [Fact]
    public void Insert_RejectsPositionsTooCloseToAnExistingAnchor()
    {
        var keys = CurveKeys.FromControlPoints(new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1f));

        Assert.Null(CurveKeys.Insert(keys, MathF.BitDecrement(CurveKeys.MinGap)));
        Assert.Null(CurveKeys.Insert(keys, 1f - CurveKeys.MinGap * 0.5f));
        Assert.Null(CurveKeys.Insert(keys, 0f));
        Assert.Null(CurveKeys.Insert(keys, 1f));
        Assert.Null(CurveKeys.Insert(keys, 2f));
    }

    [Fact]
    public void Insert_StopsAtMaxKeys()
    {
        var keys = CurveKeys.FromControlPoints(new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1f));
        for (int i = 0; i < CurveKeys.MaxKeys - 2; i++)
        {
            Assert.True(CurveKeys.CanInsert(keys));
            var grown = CurveKeys.Insert(keys, 0.1f + i * 0.1f);
            Assert.NotNull(grown);
            keys = [.. grown!];
        }

        Assert.Equal(CurveKeys.MaxKeys, keys.Length);
        Assert.False(CurveKeys.CanInsert(keys));
        Assert.Null(CurveKeys.Insert(keys, 0.95f));
    }

    // ── Remove / move ────────────────────────────────────────────────────────

    [Fact]
    public void Remove_DropsAnInteriorAnchorAndRefusesThePinnedEnds()
    {
        var keys = HoldCurve();

        Assert.Null(CurveKeys.Remove(keys, 0));
        Assert.Null(CurveKeys.Remove(keys, keys.Count - 1));
        Assert.Null(CurveKeys.Remove(keys, 99));

        var reduced = CurveKeys.Remove(keys, 1);
        Assert.NotNull(reduced);
        Assert.Equal(3, reduced!.Count);
        Assert.Equal(0.7f, reduced[1].Pos.X, precision: 5);
    }

    [Fact]
    public void Remove_RefusesToGoBelowTheTwoEndpoints()
    {
        var keys = CurveKeys.FromControlPoints(new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1f));
        Assert.Null(CurveKeys.Remove(keys, 1));
    }

    [Fact]
    public void MoveAnchor_KeepsInteriorAnchorsBetweenTheirNeighbours()
    {
        var keys  = HoldCurve();
        var moved = CurveKeys.MoveAnchor(keys, 1, new Vector2(0.99f, 0.4f));

        Assert.Equal(keys[2].Pos.X - CurveKeys.MinGap, moved[1].Pos.X, precision: 5);
        Assert.True(moved[1].Pos.X < moved[2].Pos.X);
    }

    [Fact]
    public void MoveAnchor_ClampsYToTheCanvasBounds()
    {
        var keys = HoldCurve();

        Assert.Equal(CurveKeys.YMax, CurveKeys.MoveAnchor(keys, 1, new Vector2(0.3f,  99f)).Pos(1).Y, precision: 5);
        Assert.Equal(CurveKeys.YMin, CurveKeys.MoveAnchor(keys, 1, new Vector2(0.3f, -99f)).Pos(1).Y, precision: 5);
    }

    [Fact]
    public void MoveAnchor_LeavesThePinnedEndpointsAlone()
    {
        var keys = HoldCurve();

        Assert.Equal(Vector2.Zero, CurveKeys.MoveAnchor(keys, 0, new Vector2(0.5f, 0.5f))[0].Pos);
        Assert.Equal(Vector2.One,  CurveKeys.MoveAnchor(keys, keys.Count - 1, new Vector2(0.5f, 0.5f))[^1].Pos);
    }

    [Fact]
    public void MoveAnchor_PullsHandlesBackInsideTheResizedSegments()
    {
        var keys  = HoldCurve();
        var moved = CurveKeys.MoveAnchor(keys, 1, new Vector2(0.05f, 0.25f));

        // The first segment is now 0.05 wide, so key 0's outgoing handle (was reaching 0.1) and
        // key 1's incoming handle (was reaching back 0.1) can no longer cross each other's anchor.
        Assert.True(moved[0].Out.X <= 0.05f + 1e-5f);
        Assert.True(moved[1].In.X  >= -0.05f - 1e-5f);
    }

    [Fact]
    public void MoveHandle_ClampsIntoItsOwnSegment()
    {
        var keys = HoldCurve();

        var forward = CurveKeys.MoveHandle(keys, 0, outgoing: true, new Vector2(5f, 0f));
        Assert.Equal(0.3f, forward[0].Out.X, precision: 5); // reaches the next anchor, no further

        var backward = CurveKeys.MoveHandle(keys, 0, outgoing: true, new Vector2(-5f, 0f));
        Assert.Equal(0f, backward[0].Out.X, precision: 5);  // never behind its own anchor

        var incoming = CurveKeys.MoveHandle(keys, 2, outgoing: false, new Vector2(-5f, 0f));
        Assert.Equal(-0.4f, incoming[2].In.X, precision: 5);
    }

    [Fact]
    public void MoveHandle_ClampsHandleYToTheCanvasBounds()
    {
        var keys  = HoldCurve();
        var moved = CurveKeys.MoveHandle(keys, 1, outgoing: true, new Vector2(0.1f, 99f));

        Assert.Equal(CurveKeys.YMax, moved[1].OutPoint.Y, precision: 5);
    }

    [Fact]
    public void MoveHandle_IgnoresTheSideWithNoSegment()
    {
        var keys = HoldCurve();

        Assert.Equal(keys[0].In,  CurveKeys.MoveHandle(keys, 0, outgoing: false, new Vector2(1f, 1f))[0].In);
        Assert.Equal(keys[^1].Out, CurveKeys.MoveHandle(keys, keys.Count - 1, outgoing: true, new Vector2(1f, 1f))[^1].Out);
    }

    // ── Sanitize ─────────────────────────────────────────────────────────────

    [Fact]
    public void Sanitize_SortsByTime_PinsTheEndpoints_AndSpacesTheRest()
    {
        var messy = new List<CurveKey>
        {
            new(new Vector2(0.5f, 0.5f),  Vector2.Zero, Vector2.Zero),
            new(new Vector2(0.1f, 0.2f),  Vector2.Zero, Vector2.Zero),
            new(new Vector2(0.9f, 0.4f),  Vector2.Zero, Vector2.Zero),
            new(new Vector2(0.5f, 99f),   Vector2.Zero, Vector2.Zero),
        };

        var clean = CurveKeys.Sanitize(messy);

        Assert.Equal(Vector2.Zero, clean[0].Pos);
        Assert.Equal(Vector2.One,  clean[^1].Pos);
        for (int i = 1; i < clean.Count; i++)
            Assert.True(clean[i].Pos.X >= clean[i - 1].Pos.X + CurveKeys.MinGap - 1e-5f,
                        $"anchors {i - 1}/{i} too close: {clean[i - 1].Pos.X} / {clean[i].Pos.X}");
        Assert.All(clean, k => Assert.InRange(k.Pos.Y, CurveKeys.YMin, CurveKeys.YMax));
    }

    [Fact]
    public void Sanitize_TrimsToMaxKeys_KeepingTheFinalAnchor()
    {
        var many = new List<CurveKey>();
        for (int i = 0; i < CurveKeys.MaxKeys + 4; i++)
            many.Add(new CurveKey(new Vector2(i / (float)(CurveKeys.MaxKeys + 3), i * 0.1f),
                                  Vector2.Zero, Vector2.Zero));

        var clean = CurveKeys.Sanitize(many);

        Assert.Equal(CurveKeys.MaxKeys, clean.Count);
        Assert.Equal(Vector2.One, clean[^1].Pos);
    }

    [Fact]
    public void Sanitize_FallsBackToLinear_ForAnUnusableList()
    {
        foreach (var clean in new[] { CurveKeys.Sanitize(null), CurveKeys.Sanitize([]) })
        {
            Assert.Equal(2, clean.Count);
            for (float t = 0f; t <= 1f; t += 0.1f)
                Assert.Equal(t, CubicBezier.EvaluateCurve(t, clean), precision: 3);
        }
    }

    [Fact]
    public void Sanitize_IsIdempotent_ForHandlesThatAreNeitherFlatNorAnchoredAtZero()
    {
        // A handle already inside its bounds must come back bit-identical, not merely close: the
        // y bound applies to the handle's absolute position while the offset is what gets stored,
        // and round-tripping through that conversion would drift by an ULP on every sanitize pass.
        // Curves are sanitized on load and on opening the editor, so a drifting Sanitize would also
        // quietly restrict preset authors to handles that are flat or hang off y = 0.
        var keys = new List<CurveKey>
        {
            new(new Vector2(0f,    0f),    Vector2.Zero,                new Vector2( 0.12f,  0.37f)),
            new(new Vector2(0.45f, 0.63f), new Vector2(-0.11f, -0.29f), new Vector2( 0.13f,  0.21f)),
            new(new Vector2(1f,    1f),    new Vector2(-0.17f, -0.41f), Vector2.Zero),
        };

        var once  = CurveKeys.Sanitize(keys);
        var twice = CurveKeys.Sanitize(once);

        for (int i = 0; i < keys.Count; i++)
        {
            Assert.Equal(keys[i].In,  once[i].In);
            Assert.Equal(keys[i].Out, once[i].Out);
            Assert.Equal(once[i].Pos, twice[i].Pos);
            Assert.Equal(once[i].In,  twice[i].In);
            Assert.Equal(once[i].Out, twice[i].Out);
        }
    }

    [Fact]
    public void Sanitize_LeavesAnAlreadyValidCurveUntouched()
    {
        var keys  = HoldCurve();
        var again = CurveKeys.Sanitize(keys);

        Assert.Equal(keys.Count, again.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            Assert.Equal(keys[i].Pos, again[i].Pos);
            Assert.Equal(keys[i].In,  again[i].In);
            Assert.Equal(keys[i].Out, again[i].Out);
        }
    }
}

internal static class CurveKeyListExtensions
{
    /// <summary>Terser <c>list[i].Pos</c> for assertions that index straight into a returned list.</summary>
    public static Vector2 Pos(this List<CurveKey> keys, int index) => keys[index].Pos;
}
