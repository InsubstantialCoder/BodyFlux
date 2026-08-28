using System.Linq;
using System.Numerics;
using BodyFlux.Morph;
using Xunit;

namespace BodyFlux.Tests.Morph;

/// <summary>
/// The preset catalogue: that every shipped shape is legal by <see cref="CurveKeys"/>'s rules
/// before any editor ever touches it, that the six CSS-equivalent presets still have the exact
/// control points they have always had, and that each multi-point preset actually does the thing
/// its name promises.
/// </summary>
public class EasingCurvePresetsTests
{
    private static EasingCurvePresets.Preset Get(string name) =>
        EasingCurvePresets.All.First(p => p.Name == name);

    // Preset names, so a [Theory] can address one without repeating the whole catalogue.
    public static TheoryData<string> EveryPresetName()
    {
        var data = new TheoryData<string>();
        foreach (var preset in EasingCurvePresets.All)
            data.Add(preset.Name);
        return data;
    }

    // ── Catalogue invariants ─────────────────────────────────────────────────

    [Fact]
    public void All_StartsWithLinear()
    {
        // Callers take All[0] as the blank-slate curve (the editor's "New" starts there), so the
        // order is part of the contract, not just presentation.
        Assert.Equal("Linear", EasingCurvePresets.All[0].Name);
    }

    [Fact]
    public void All_NamesAreUnique()
    {
        // The combo picks a preset by name in a couple of places, and the tests below all look one
        // up that way.
        var names = EasingCurvePresets.All.Select(p => p.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(EveryPresetName))]
    public void Preset_IsAFixedPointOfSanitize(string name)
    {
        // The load-time repair pass must have nothing to repair: a preset that Sanitize rewrites
        // would be a shape the user cannot re-create by hand in the editor, and would silently
        // change the moment it was opened there.
        var keys      = Get(name).Keys;
        var sanitized = CurveKeys.Sanitize(keys);

        Assert.Equal(keys.Length, sanitized.Count);
        for (int i = 0; i < keys.Length; i++)
            Assert.Equal(keys[i], sanitized[i]);
    }

    [Theory]
    [MemberData(nameof(EveryPresetName))]
    public void Preset_HasPinnedEndpoints(string name)
    {
        var keys = Get(name).Keys;

        Assert.Equal(Vector2.Zero, keys[0].Pos);
        Assert.Equal(Vector2.One,  keys[^1].Pos);
    }

    [Theory]
    [MemberData(nameof(EveryPresetName))]
    public void Preset_KeyCount_IsWithinBounds(string name)
    {
        var keys = Get(name).Keys;

        Assert.True(keys.Length >= 2, $"{name} has fewer than two anchors");
        Assert.True(keys.Length <= CurveKeys.MaxKeys, $"{name} has {keys.Length} anchors, over the {CurveKeys.MaxKeys} cap");
    }

    [Theory]
    [MemberData(nameof(EveryPresetName))]
    public void Preset_AnchorsAreStrictlyIncreasingInX_WithMinGap(string name)
    {
        var keys = Get(name).Keys;

        for (int i = 1; i < keys.Length; i++)
        {
            float gap = keys[i].Pos.X - keys[i - 1].Pos.X;
            Assert.True(gap >= CurveKeys.MinGap - 1e-6f,
                $"{name}: anchors {i - 1}->{i} are only {gap} apart, under MinGap {CurveKeys.MinGap}");
        }
    }

    [Theory]
    [MemberData(nameof(EveryPresetName))]
    public void Preset_HandlesStayInsideTheirOwnSegment(string name)
    {
        // The invariant that keeps a segment from doubling back on itself in time. Checked here
        // directly rather than only through Sanitize, so a bad handle names the preset it is in.
        var keys = Get(name).Keys;

        for (int i = 0; i < keys.Length - 1; i++)
        {
            float span = keys[i + 1].Pos.X - keys[i].Pos.X;
            Assert.InRange(keys[i].Out.X,     0f,    span);
            Assert.InRange(keys[i + 1].In.X, -span,  0f);
            Assert.InRange(keys[i].OutPoint.Y,    CurveKeys.YMin, CurveKeys.YMax);
            Assert.InRange(keys[i + 1].InPoint.Y, CurveKeys.YMin, CurveKeys.YMax);
        }
    }

    [Theory]
    [MemberData(nameof(EveryPresetName))]
    public void Preset_EvaluatesToZeroAtStart_AndOneAtEnd(string name)
    {
        // A morph must land exactly on its destination profile whatever route the curve took to
        // get there, so this holds for the overshoot shapes too.
        var keys = Get(name).Keys;

        Assert.Equal(0f, CubicBezier.EvaluateCurve(0f, keys), precision: 5);
        Assert.Equal(1f, CubicBezier.EvaluateCurve(1f, keys), precision: 5);
    }

    // ── Legacy control points ────────────────────────────────────────────────

    [Theory]
    [InlineData("Linear",      0f,    0f,    1f,    1f)]
    [InlineData("Ease",        0.25f, 0.1f,  0.25f, 1f)]
    [InlineData("Ease In",     0.42f, 0f,    1f,    1f)]
    [InlineData("Ease Out",    0f,    0f,    0.58f, 1f)]
    [InlineData("Ease In-Out", 0.42f, 0f,    0.58f, 1f)]
    [InlineData("Back",        0.34f, 1.56f, 0.64f, 1f)]
    public void LegacyPreset_KeepsItsExactHistoricalControlPoints(
        string name, float p1x, float p1y, float p2x, float p2y)
    {
        // Hardcoded from the pre-multi-point EasingCurvePresets, not read back from the key list.
        // These six are what every curve saved before multi-point support was authored from, and a
        // user who picks "Ease" today must get the same shape they got a year ago.
        var preset = Get(name);

        Assert.Equal(new Vector2(p1x, p1y), preset.P1);
        Assert.Equal(new Vector2(p2x, p2y), preset.P2);
        Assert.False(preset.IsMultiPoint, $"{name} should still be a plain two-point curve");
    }

    [Theory]
    [InlineData("Bounce")]
    [InlineData("Anticipate")]
    [InlineData("Hold")]
    [InlineData("Steps")]
    public void NewPreset_IsMultiPoint(string name)
    {
        Assert.True(Get(name).IsMultiPoint, $"{name} is supposed to carry interior anchors");
    }

    // ── Each multi-point preset does what its name says ──────────────────────

    [Fact]
    public void Bounce_OvershootsPastTheDestination_ThenRingsDownAndSettles()
    {
        var keys = Get("Bounce").Keys;

        float peak = Enumerable.Range(0, 101).Max(i => CubicBezier.EvaluateCurve(i / 100f, keys));
        Assert.True(peak > 1.10f, $"Bounce never really overshoots (peak {peak})");

        // Comes back past the destination after the first swing, so the ring-down is visible and
        // not just a single "Back"-style overshoot.
        Assert.True(CubicBezier.EvaluateCurve(0.60f, keys) < 0.95f, "Bounce does not fall back below the destination");

        // Second swing is smaller than the first, and the whole thing lands on 1.
        Assert.True(CubicBezier.EvaluateCurve(0.78f, keys) < peak, "Bounce's second swing is not smaller than its first");
        Assert.Equal(1f, CubicBezier.EvaluateCurve(1f, keys), precision: 5);
    }

    [Fact]
    public void Anticipate_DipsBelowTheStart_BeforeMovingToTheDestination()
    {
        var keys = Get("Anticipate").Keys;

        float dip = Enumerable.Range(0, 51).Min(i => CubicBezier.EvaluateCurve(i / 100f, keys));
        Assert.True(dip < -0.05f, $"Anticipate never winds up below the starting profile (lowest {dip})");

        // The wind-up is the whole point, so it has to happen before the move, not during it.
        Assert.True(CubicBezier.EvaluateCurve(0.26f, keys) < 0f, "Anticipate is already back above the start mid wind-up");
        Assert.True(CubicBezier.EvaluateCurve(0.82f, keys) > 1f, "Anticipate does not swing through past the destination");
    }

    [Fact]
    public void Hold_IsExactlyFlatAcrossItsPlateau()
    {
        // The plateau's two anchors share a y and face it with horizontal handles, which makes the
        // segment between them constant rather than merely slow: the morph parks, it does not creep.
        var keys = Get("Hold").Keys;

        for (float t = 0.30f; t <= 0.62f; t += 0.02f)
            Assert.Equal(0.5f, CubicBezier.EvaluateCurve(t, keys), precision: 4);

        // And it is a pause in the middle of a real move, not a flat curve throughout.
        Assert.True(CubicBezier.EvaluateCurve(0.15f, keys) < 0.5f);
        Assert.True(CubicBezier.EvaluateCurve(0.85f, keys) > 0.5f);
    }

    [Fact]
    public void Steps_IsFlatAcrossBothTreads()
    {
        var keys = Get("Steps").Keys;

        for (float t = 0.22f; t <= 0.40f; t += 0.02f)
            Assert.Equal(0.33f, CubicBezier.EvaluateCurve(t, keys), precision: 4);

        for (float t = 0.60f; t <= 0.78f; t += 0.02f)
            Assert.Equal(0.66f, CubicBezier.EvaluateCurve(t, keys), precision: 4);

        // Three distinct rises between the treads.
        Assert.True(CubicBezier.EvaluateCurve(0.11f, keys) is > 0f    and < 0.33f);
        Assert.True(CubicBezier.EvaluateCurve(0.50f, keys) is > 0.33f and < 0.66f);
        Assert.True(CubicBezier.EvaluateCurve(0.89f, keys) is > 0.66f and < 1f);
    }
}
