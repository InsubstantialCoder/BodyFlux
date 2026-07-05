using System.Numerics;
using BodyFlux.Morph;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BodyFlux.Tests.Morph;

public class BoneJsonHelperTests
{
    [Fact]
    public void ReadBoneTransform_MissingBone_ReturnsIdentity()
    {
        var bones = new JObject();
        var (t, r, s) = BoneJsonHelper.ReadBoneTransform(bones, "n_root");

        Assert.Equal(Vector3.Zero, t);
        Assert.Equal(Vector3.Zero, r);
        Assert.Equal(Vector3.One, s);
    }

    [Fact]
    public void ReadBoneTransform_RoundTripsWrittenValues()
    {
        var bones = new JObject();
        var translation = new Vector3(1, 2, 3);
        var rotation    = new Vector3(4, 5, 6);
        var scale       = new Vector3(1.5f, 1.5f, 1.5f);

        BoneJsonHelper.SetBoneTransform(bones, "j_kao", translation, rotation, scale);
        var (t, r, s) = BoneJsonHelper.ReadBoneTransform(bones, "j_kao");

        Assert.Equal(translation, t);
        Assert.Equal(rotation, r);
        Assert.Equal(scale, s);
    }

    [Fact]
    public void SetBoneTransform_ExistingBone_PreservesMetadata()
    {
        var bones = new JObject
        {
            ["j_kao"] = new JObject
            {
                ["Translation"] = BoneJsonHelper.Vec3Json(Vector3.Zero),
                ["Rotation"]    = BoneJsonHelper.Vec3Json(Vector3.Zero),
                ["Scaling"]     = BoneJsonHelper.Vec3Json(Vector3.One),
                ["PropagateScale"] = true,
                ["ChildScaleIndependent"] = true,
            },
        };

        BoneJsonHelper.SetBoneTransform(bones, "j_kao", Vector3.One, Vector3.One, new Vector3(2));

        var bone = (JObject)bones["j_kao"]!;
        Assert.True(bone["PropagateScale"]!.Value<bool>());
        Assert.True(bone["ChildScaleIndependent"]!.Value<bool>());
    }

    [Fact]
    public void IsBoneLinked_TrueOnlyWhenPropagateScaleSet()
    {
        var bones = new JObject
        {
            ["linked"]   = new JObject { ["PropagateScale"] = true },
            ["unlinked"] = new JObject { ["PropagateScale"] = false },
            ["absent"]   = new JObject(),
        };

        Assert.True(BoneJsonHelper.IsBoneLinked(bones, "linked"));
        Assert.False(BoneJsonHelper.IsBoneLinked(bones, "unlinked"));
        Assert.False(BoneJsonHelper.IsBoneLinked(bones, "absent"));
        Assert.False(BoneJsonHelper.IsBoneLinked(bones, "missing"));
    }

    [Fact]
    public void SetLinkedChildScaling_SetsPropagateScaleAndIndependentFlag()
    {
        var bones = new JObject { ["j_asi_a"] = new JObject() };

        BoneJsonHelper.SetLinkedChildScaling(bones, "j_asi_a", new Vector3(1.2f));

        var bone = (JObject)bones["j_asi_a"]!;
        Assert.True(bone["PropagateScale"]!.Value<bool>());
        Assert.True(bone["ChildScaleIndependent"]!.Value<bool>());
        Assert.Equal(1.2f, bone["ChildScaling"]!["X"]!.Value<float>(), precision: 5);
    }

    [Fact]
    public void CloneBoneTemplate_PrefersDestinationOverOrigin()
    {
        var origin = new JObject { ["j_kao"] = new JObject { ["marker"] = "origin" } };
        var dest   = new JObject { ["j_kao"] = new JObject { ["marker"] = "dest" } };

        var template = BoneJsonHelper.CloneBoneTemplate(origin, dest, "j_kao");

        Assert.Equal("dest", template!["marker"]!.Value<string>());
    }

    [Fact]
    public void CloneBoneTemplate_FallsBackToOrigin_WhenDestMissing()
    {
        var origin = new JObject { ["j_kao"] = new JObject { ["marker"] = "origin" } };
        var dest   = new JObject();

        var template = BoneJsonHelper.CloneBoneTemplate(origin, dest, "j_kao");

        Assert.Equal("origin", template!["marker"]!.Value<string>());
    }

    [Fact]
    public void CloneBoneTemplate_ReturnsNull_WhenBoneMissingFromBoth()
    {
        var origin = new JObject();
        var dest   = new JObject();

        Assert.Null(BoneJsonHelper.CloneBoneTemplate(origin, dest, "j_kao"));
    }

    [Fact]
    public void BuildOverlayDestination_OverlaysOnlySpecifiedBones()
    {
        var origin = new JObject
        {
            ["j_kao"]  = new JObject { ["marker"] = "origin-kao" },
            ["j_mune"] = new JObject { ["marker"] = "origin-mune" },
        };
        var overlay = new JObject
        {
            ["j_kao"] = new JObject { ["marker"] = "overlay-kao" },
        };

        var merged = BoneJsonHelper.BuildOverlayDestination(origin, overlay);

        Assert.Equal("overlay-kao", merged["j_kao"]!["marker"]!.Value<string>());
        Assert.Equal("origin-mune", merged["j_mune"]!["marker"]!.Value<string>());
    }

    [Fact]
    public void BuildOverlayDestination_DoesNotMutateOriginalBones()
    {
        var origin  = new JObject { ["j_kao"] = new JObject { ["marker"] = "origin" } };
        var overlay = new JObject { ["j_kao"] = new JObject { ["marker"] = "overlay" } };

        BoneJsonHelper.BuildOverlayDestination(origin, overlay);

        Assert.Equal("origin", origin["j_kao"]!["marker"]!.Value<string>());
    }
}
