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
    [InlineData("Breathing")]
    [InlineData("Throbbing")]
    [InlineData("Surge")]
    [InlineData("Hesitate")]
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
    public void Surge_ArrivesInThreeWaves_EachCarryingFurtherThanTheLast()
    {
        var keys = Get("Surge").Keys;
        float At(float t) => CubicBezier.EvaluateCurve(t, keys);

        // Wave crests and the shallow relaxations between them.
        float crest1 = At(0.22f), relax1 = At(0.36f);
        float crest2 = At(0.56f), relax2 = At(0.70f);

        Assert.True(relax1 < crest1, "Surge does not ease back after its first wave");
        Assert.True(relax2 < crest2, "Surge does not ease back after its second wave");
        Assert.True(crest2 > crest1, "Surge's second wave does not carry further than its first");

        // Each wave gains more ground than the one before, which is what separates gathering
        // strength from a stutter.
        Assert.True(crest2 - relax1 > crest1, "Surge's second wave is not the bigger climb");
        Assert.True(1f - relax2 > crest2 - relax1, "Surge's final wave is not the biggest climb");

        // A surge builds towards the destination, it does not fly past it.
        for (int i = 0; i <= 200; i++)
            Assert.True(At(i / 200f) <= 1f + 1e-4f, $"Surge overshoots at t={i / 200f}");
    }

    [Fact]
    public void Hesitate_CommitsPastHalfway_PullsBack_ThenGoesAllTheWay()
    {
        var keys = Get("Hesitate").Keys;
        float At(float t) => CubicBezier.EvaluateCurve(t, keys);

        float commit = At(0.24f);
        float doubt  = At(0.48f);

        Assert.True(commit > 0.5f,         $"Hesitate never really sets off ({commit})");
        Assert.True(doubt < commit - 0.1f, $"Hesitate does not pull back ({doubt} against {commit})");
        Assert.True(doubt > 0f, "Hesitate should waver mid-move, not retreat to the origin");

        // Unlike Anticipate, the doubt happens after the move has started, not before it.
        Assert.True(At(0.10f) > 0.15f, "Hesitate hangs around the origin instead of setting off first");

        // Then it commits for real.
        Assert.True(At(0.74f) > 0.9f, "Hesitate does not commit after wavering");
        Assert.Equal(1f, At(1f), precision: 5);

        // And it stays a plain transition between the two profiles, no overshoot either side.
        for (int i = 0; i <= 200; i++)
            Assert.InRange(At(i / 200f), -1e-4f, 1f + 1e-4f);
    }

    [Fact]

    public void Breathing_RisesWithoutEverBackingUp_AndNeverPassesTheDestination()

    {

        // A breath in only goes one way, and a morph that overshot here would read as a gasp. Both

        // matter more than usual for this preset because it is meant to be left running on Loop

        // (Infinite), where any wobble repeats forever.

        var keys = Get("Breathing").Keys;



        float previous = 0f;

        for (int i = 0; i <= 200; i++)

        {

            float value = CubicBezier.EvaluateCurve(i / 200f, keys);

            Assert.True(value >= previous - 1e-4f, $"Breathing backs up at t={i / 200f}");

            Assert.True(value <= 1f + 1e-4f, $"Breathing overshoots the destination at t={i / 200f} ({value})");

            previous = value;

        }

    }



    [Fact]

    public void Breathing_StopsExactlyAtFullExtension_ForThePauseAtTheTopOfABreath()

    {

        // The last two anchors share y = 1 with horizontal handles, so this really is a stop and

        // not a slow crawl. Under Loop (Infinite) the reverse pass replays it as the pause before

        // breathing out.

        var keys = Get("Breathing").Keys;



        for (float t = 0.78f; t <= 1f; t += 0.02f)

            Assert.Equal(1f, CubicBezier.EvaluateCurve(t, keys), precision: 5);



        // And it is still a real move before that, not a curve that arrives immediately.

        Assert.True(CubicBezier.EvaluateCurve(0.20f, keys) < 0.2f);

        Assert.True(CubicBezier.EvaluateCurve(0.50f, keys) is > 0.55f and < 0.8f);

    }



    [Fact]

    public void Breathing_EasesOffBothEnds_SoALoopedBreathHasNoKinkAtTheTurnaround()

    {

        // Near-zero speed at t=0 and t=1. Loop (Infinite) reverses direction at those points, and

        // any real speed there would show up as a jolt once per breath.

        var keys = Get("Breathing").Keys;



        float startSpeed = (CubicBezier.EvaluateCurve(0.01f, keys) - CubicBezier.EvaluateCurve(0f, keys)) / 0.01f;

        float endSpeed   = (CubicBezier.EvaluateCurve(1f, keys)    - CubicBezier.EvaluateCurve(0.99f, keys)) / 0.01f;



        Assert.True(startSpeed < 0.15f, $"Breathing leaves the origin too fast ({startSpeed})");

        Assert.True(endSpeed   < 0.05f, $"Breathing arrives too fast ({endSpeed})");

    }



    [Fact]

    public void Throbbing_BeatsTwice_TheSecondSmallerThanTheFirst_ThenRests()

    {

        var keys = Get("Throbbing").Keys;

        float At(float t) => CubicBezier.EvaluateCurve(t, keys);



        float firstBeat  = At(0.18f);

        float recoil     = At(0.32f);

        float secondBeat = At(0.48f);



        Assert.True(firstBeat > 1.10f,  $"Throbbing's first beat does not punch past the destination ({firstBeat})");

        Assert.True(recoil    < 0.80f,  $"Throbbing does not recoil between beats ({recoil})");

        Assert.True(secondBeat > recoil, "Throbbing's second beat does not rise again");

        Assert.True(secondBeat < firstBeat, "Throbbing's second beat should be the smaller of the two");



        // Both beats land inside the first half, leaving the back of the run as the rest between

        // beats. Evenly spread pulses would read as a machine rather than a heart.

        Assert.True(At(0.80f) is > 0.9f and < 1f, "Throbbing does not drift quietly into place after the beats");

        Assert.Equal(1f, At(1f), precision: 5);

    }



    [Fact]

    public void Throbbing_StartsGently_SoALoopedPulseDoesNotPopAtTheSeam()

    {

        // The beats are supposed to be the only sharp events in the cycle; the join between one

        // pass and the next is not.

        var keys = Get("Throbbing").Keys;



        float seamSpeed = (CubicBezier.EvaluateCurve(0.01f, keys) - CubicBezier.EvaluateCurve(0f, keys)) / 0.01f;

        float beatSpeed = (CubicBezier.EvaluateCurve(0.10f, keys) - CubicBezier.EvaluateCurve(0.09f, keys)) / 0.01f;



        Assert.True(seamSpeed < beatSpeed / 4f,

                    $"Throbbing leaves the origin nearly as fast as it beats ({seamSpeed} vs {beatSpeed})");

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
