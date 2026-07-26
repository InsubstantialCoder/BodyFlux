using System.Numerics;
using BodyFlux.Morph;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BodyFlux.Tests.Morph;

public class MorphControllerTests
{
    private static JObject Bone(Vector3 translation, Vector3 rotation, Vector3 scale) => new()
    {
        ["Translation"] = BoneJsonHelper.Vec3Json(translation),
        ["Rotation"]    = BoneJsonHelper.Vec3Json(rotation),
        ["Scaling"]     = BoneJsonHelper.Vec3Json(scale),
    };

    private static (JObject profileBase, JObject origin, JObject dest) BuildProfiles(
        Vector3 originScale, Vector3 destScale)
    {
        var origin = new JObject { ["n_root"] = Bone(Vector3.Zero, Vector3.Zero, originScale) };
        var dest   = new JObject { ["n_root"] = Bone(Vector3.Zero, Vector3.Zero, destScale) };
        var profileBase = new JObject { ["Bones"] = new JObject() };
        return (profileBase, origin, dest);
    }

    [Fact]
    public void Start_SeedsWorkingProfileAtOriginValues()
    {
        var controller = new MorphController();
        var (profileBase, origin, dest) = BuildProfiles(Vector3.One, new Vector3(1.5f));

        controller.Start(0, profileBase, origin, dest);

        Assert.True(controller.IsMorphing);
        Assert.Equal(0f, controller.Progress);
        Assert.Equal(1, controller.BoneCount);
    }

    [Fact]
    public void Tick_Simple_StopsAtDestinationAndInterpolatesLinearly()
    {
        var controller = new MorphController();
        var (profileBase, origin, dest) = BuildProfiles(Vector3.One, new Vector3(2f));

        controller.Start(0, profileBase, origin, dest, MorphMode.Simple, EasingMode.Linear);

        var json = controller.Tick(seconds: 0.5f, speed: 1f); // progress 0 -> 0.5
        Assert.NotNull(json);
        var mid = JObject.Parse(json!);
        Assert.Equal(1.5f, mid["Bones"]!["n_root"]!["Scaling"]!["X"]!.Value<float>(), precision: 4);
        Assert.True(controller.IsMorphing);

        controller.Tick(seconds: 0.5f, speed: 1f); // progress 0.5 -> 1.0, hits boundary
        Assert.False(controller.IsMorphing);
        Assert.Equal(1f, controller.Progress);
        Assert.True(controller.IsFinished);
    }

    [Fact]
    public void Tick_ReturnsNull_WhenNotMorphing()
    {
        var controller = new MorphController();
        Assert.Null(controller.Tick(0.1f, 1f));
    }

    [Fact]
    public void Tick_LoopSingle_ReversesAtDestinationAndStopsAtOrigin()
    {
        var controller = new MorphController();
        var (profileBase, origin, dest) = BuildProfiles(Vector3.One, new Vector3(2f));

        controller.Start(0, profileBase, origin, dest, MorphMode.LoopSingle);

        controller.Tick(1f, 1f); // reach destination, direction flips
        Assert.True(controller.IsMorphing);
        Assert.Equal(1f, controller.Progress);

        controller.Tick(1f, 1f); // back to origin, stops
        Assert.False(controller.IsMorphing);
        Assert.Equal(0f, controller.Progress);
    }

    [Fact]
    public void Tick_LoopInfinite_PingPongsUntilStopped()
    {
        var controller = new MorphController();
        var (profileBase, origin, dest) = BuildProfiles(Vector3.One, new Vector3(2f));

        controller.Start(0, profileBase, origin, dest, MorphMode.LoopInfinite);

        controller.Tick(1f, 1f); // to destination
        controller.Tick(1f, 1f); // back to origin, should keep going forward
        Assert.True(controller.IsMorphing);

        controller.Stop();
        Assert.False(controller.IsMorphing);
        Assert.Equal(0, controller.BoneCount);
    }

    [Fact]
    public void Pause_And_Resume_PreserveProgress()
    {
        var controller = new MorphController();
        var (profileBase, origin, dest) = BuildProfiles(Vector3.One, new Vector3(2f));

        controller.Start(0, profileBase, origin, dest, MorphMode.Simple);
        controller.Tick(0.3f, 1f);
        controller.Pause();

        Assert.False(controller.IsMorphing);
        Assert.True(controller.IsPaused);
        var progressAtPause = controller.Progress;

        controller.Resume();
        Assert.True(controller.IsMorphing);
        Assert.Equal(progressAtPause, controller.Progress);
    }

    [Fact]
    public void Reverse_WhileMorphing_FlipsDirection()
    {
        var controller = new MorphController();
        var (profileBase, origin, dest) = BuildProfiles(Vector3.One, new Vector3(2f));

        controller.Start(0, profileBase, origin, dest, MorphMode.Simple);
        controller.Tick(0.5f, 1f); // progress 0.5, moving forward

        controller.Reverse();
        controller.Tick(0.5f, 1f); // should move back toward origin now

        Assert.True(controller.Progress < 0.5f);
    }

    [Fact]
    public void BeginReset_SweepsBackToOriginAndStopsEvenInLoopInfinite()
    {
        var controller = new MorphController();
        var (profileBase, origin, dest) = BuildProfiles(Vector3.One, new Vector3(2f));

        controller.Start(0, profileBase, origin, dest, MorphMode.LoopInfinite);
        controller.Tick(0.5f, 1f); // partway to destination

        controller.BeginReset();
        Assert.True(controller.IsResetting);

        controller.Tick(1f, 1f); // reverse sweep should hit 0 and stop, not bounce
        Assert.False(controller.IsMorphing);
        Assert.Equal(0f, controller.Progress);
    }

    [Fact]
    public void ExternaliseRoot_OmitsRootFromJsonAndExposesCurrentRootScale()
    {
        var controller = new MorphController();
        var (profileBase, origin, dest) = BuildProfiles(Vector3.One, new Vector3(2f));

        controller.Start(0, profileBase, origin, dest, MorphMode.Simple, EasingMode.Linear,
            externaliseRoot: true);

        Assert.Equal(Vector3.One, controller.CurrentRootScale);

        var json = controller.Tick(0.5f, 1f);
        var bones = JObject.Parse(json!)["Bones"] as JObject;

        Assert.False(bones!.ContainsKey("n_root"));
        Assert.Equal(1.5f, controller.CurrentRootScale!.Value.X, precision: 4);
    }

    [Fact]
    public void Start_IndependentChildScaling_PreservesExplicitChildScale_NotParentScale()
    {
        // Reported bug: a waist bone with independent child scaling (parent 1.15/1.25/1.15, children
        // an explicit 1.02/1.04/1.02) was morphed as if linked — the children were scaled to the
        // parent's 1.15 instead of the intended 1.02. The morph must keep the explicit ChildScaling.
        var controller = new MorphController();

        var waist = Bone(Vector3.Zero, Vector3.Zero, new Vector3(1.15f, 1.25f, 1.15f));
        waist["PropagateScale"]        = true;                 // independent bones still propagate
        waist["ChildScaleIndependent"] = true;
        waist["ChildScaling"]          = new JObject { ["X"] = 1.02f, ["Y"] = 1.04f, ["Z"] = 1.02f };

        var origin      = new JObject { ["j_sebo_c"] = waist };
        var dest        = new JObject { ["j_sebo_c"] = (JObject)waist.DeepClone() }; // static: no change
        var profileBase = new JObject { ["Bones"] = new JObject() };

        controller.Start(0, profileBase, origin, dest);

        // At the origin (progress 0) the child scale must be the user's 1.02/1.04/1.02, never 1.15.
        var json = controller.Tick(0.001f, 1f);
        var bone = JObject.Parse(json!)["Bones"]!["j_sebo_c"]!;

        Assert.True(bone["ChildScaleIndependent"]!.Value<bool>());
        Assert.True(bone["PropagateScale"]!.Value<bool>()); // gate kept open so C+ applies it
        Assert.Equal(1.02f, bone["ChildScaling"]!["X"]!.Value<float>(), precision: 4);
        Assert.Equal(1.04f, bone["ChildScaling"]!["Y"]!.Value<float>(), precision: 4);
        Assert.Equal(1.02f, bone["ChildScaling"]!["Z"]!.Value<float>(), precision: 4);
    }

    [Fact]
    public void Start_RemovesIdentityOnlyChannels_ToAvoidOverridingExternalPositioning()
    {
        // Both origin and dest have zero Translation/Rotation but differing Scale — the
        // Translation/Rotation channels should be stripped so an external mover (e.g. Brio)
        // isn't fought over those channels.
        var controller = new MorphController();
        var (profileBase, origin, dest) = BuildProfiles(Vector3.One, new Vector3(2f));

        controller.Start(0, profileBase, origin, dest);
        var json = controller.Tick(0.01f, 1f);
        var bone = JObject.Parse(json!)["Bones"]!["n_root"] as JObject;

        Assert.False(bone!.ContainsKey("Translation"));
        Assert.False(bone.ContainsKey("Rotation"));
    }
}
