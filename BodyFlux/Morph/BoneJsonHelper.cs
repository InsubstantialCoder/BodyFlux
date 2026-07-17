using System.Numerics;
using Newtonsoft.Json.Linq;

namespace BodyFlux.Morph;

/// <summary>
/// Pure static helpers for reading and writing bone transforms in Customize+ profile JSON.
/// All methods are side-effect free except for <see cref="SetBoneTransform"/>, which
/// mutates only the <paramref name="bones"/> node that was explicitly supplied.
/// </summary>
internal static class BoneJsonHelper
{
    // ── Primitives ────────────────────────────────────────────────────────────

    public static JObject Vec3Json(Vector3 v) =>
        new() { ["X"] = v.X, ["Y"] = v.Y, ["Z"] = v.Z };

    private static Vector3 ReadVec3(JObject? node, float defaultVal) =>
        node == null ? new Vector3(defaultVal) :
        new Vector3(
            node["X"]?.Value<float>() ?? defaultVal,
            node["Y"]?.Value<float>() ?? defaultVal,
            node["Z"]?.Value<float>() ?? defaultVal);

    // ── Bone read ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the Translation, Rotation and Scale channels from <paramref name="bones"/>[<paramref name="boneName"/>].
    /// Missing bones or channels fall back to (0,0,0) for T/R and (1,1,1) for S.
    /// </summary>
    public static (Vector3 translation, Vector3 rotation, Vector3 scale)
        ReadBoneTransform(JObject bones, string boneName)
    {
        if (bones[boneName] is not JObject bone)
            return (Vector3.Zero, Vector3.Zero, Vector3.One);

        return (
            ReadVec3(bone["Translation"] as JObject, 0f),
            ReadVec3(bone["Rotation"]    as JObject, 0f),
            ReadVec3(bone["Scaling"]     as JObject, 1f));
    }

    /// <summary>
    /// Returns a deep clone of the bone's full JSON entry from whichever source profile defines it
    /// (destination takes priority, then origin), or null if neither has it. Used as the working-
    /// profile template so Customize+ metadata (PropagateScale/Rotation/Translation,
    /// ChildScalingIndependent, ChildScaling, …) survives the morph untouched.
    /// </summary>
    public static JObject? CloneBoneTemplate(JObject originBones, JObject destBones, string boneName)
    {
        if (destBones[boneName] is JObject d) return (JObject)d.DeepClone();
        if (originBones[boneName] is JObject o) return (JObject)o.DeepClone();
        return null;
    }

    /// <summary>
    /// True when the bone's entry in <paramref name="bones"/> is a Customize+ *linked* scale parent:
    /// its scale propagates to child bones (PropagateScale set) AND it is NOT in independent-child-
    /// scaling mode — i.e. the children simply follow the parent's own scale. Independent bones are
    /// deliberately excluded (they also set PropagateScale) because their child magnitude is an
    /// explicit value the user chose, not the parent's scale; conflating the two made a morph
    /// overwrite an independent 1.02 child scale with the parent's 1.15. See
    /// <see cref="IsChildScaleIndependent"/>, <see cref="ReadChildScaling"/>.
    ///
    /// Evaluated per endpoint (no origin/destination precedence): a morph must know the link state of
    /// BOTH ends independently so it can ramp the child magnitude from the origin's propagated scale
    /// to the destination's — see <see cref="SetLinkedChildScaling"/> and MorphController's BoneAnim.
    /// </summary>
    public static bool IsBoneLinked(JObject bones, string boneName) =>
        bones[boneName] is JObject b
        && b["PropagateScale"]?.Value<bool>() == true
        && !IsChildScaleIndependent(bones, boneName);

    /// <summary>
    /// True when the bone uses Customize+ *independent* child scaling — the child bones carry their
    /// own explicit <see cref="ReadChildScaling"/> vector instead of following the parent's scale.
    ///
    /// Reads BOTH spellings of the flag because the source can arrive under either: Customize+'s IPC
    /// schema (IPCBoneTransform, e.g. GetTemplate) names it <c>ChildScaleIndependent</c>, while its
    /// on-disk profile format (as GetByUniqueId may serialise it) names it <c>ChildScalingIndependent</c>.
    /// A morph must detect independence regardless of which one the origin/destination JSON carries;
    /// missing it makes Customize+ fall back to linked mode and scale the children to the parent's size.
    /// </summary>
    public static bool IsChildScaleIndependent(JObject bones, string boneName) =>
        bones[boneName] is JObject b
        && (b["ChildScaleIndependent"]?.Value<bool>() == true
            || b["ChildScalingIndependent"]?.Value<bool>() == true);

    /// <summary>
    /// Reads the bone's explicit ChildScaling vector (the independent child-scale magnitude), falling
    /// back to identity when the bone or the channel is absent. Only meaningful when
    /// <see cref="IsChildScaleIndependent"/> is true.
    /// </summary>
    public static Vector3 ReadChildScaling(JObject bones, string boneName) =>
        bones[boneName] is JObject b ? ReadVec3(b["ChildScaling"] as JObject, 1f) : Vector3.One;

    /// <summary>
    /// True when the bone's entry in <paramref name="bones"/> has Customize+ "chain" (propagate)
    /// enabled for translation. Read per endpoint, like <see cref="IsBoneLinked"/>.
    /// </summary>
    public static bool IsPropagateTranslation(JObject bones, string boneName) =>
        bones[boneName] is JObject b && b["PropagateTranslation"]?.Value<bool>() == true;

    /// <summary>
    /// True when the bone's entry in <paramref name="bones"/> has Customize+ "chain" (propagate)
    /// enabled for rotation. Read per endpoint, like <see cref="IsBoneLinked"/>.
    /// </summary>
    public static bool IsPropagateRotation(JObject bones, string boneName) =>
        bones[boneName] is JObject b && b["PropagateRotation"]?.Value<bool>() == true;

    /// <summary>
    /// Force the translation/rotation propagation ("chain") flags true on an existing working bone so
    /// they reach Customize+.
    ///
    /// Confirmed by dumping the exact JSON BodyFlux sends versus the Customize+ IPC Test tab's working
    /// profile: C+ DOES propagate translation/rotation to child bones through
    /// SetTemporaryProfileOnCharacter (its own IPC round-trip applies a chained Head correctly). The
    /// only difference was that BodyFlux emitted <c>PropagateRotation:false</c> — the flag is lost
    /// through the GetProfile → clone → SetBoneTransform working document, exactly like PropagateScale
    /// (see <see cref="SetLinkedChildScaling"/>, which re-asserts it). Unlike scale, T/R needs no
    /// child magnitude: C+ derives the delta from the bone's own transform each frame, so re-asserting
    /// the flag on a bone that carries a non-zero Translation/Rotation is sufficient. Only ever sets
    /// true, never clears, so a bone that does not chain is left untouched.
    /// </summary>
    public static void SetPropagateFlags(JObject bones, string boneName, bool translation, bool rotation)
    {
        if (bones[boneName] is not JObject bone) return;
        if (translation) bone["PropagateTranslation"] = true;
        if (rotation)    bone["PropagateRotation"]    = true;
    }

    /// <summary>
    /// Builds a virtual destination "Bones" node for <see cref="MorphTargetMode.TemplateOverlay"/>: a
    /// deep clone of <paramref name="originBones"/> with every bone present in
    /// <paramref name="overlayBones"/> replaced by that bone's entry. Bones absent from the overlay
    /// stay identical to the origin, so <see cref="MorphController"/>'s ordinary origin/destination
    /// interpolation leaves them static — only the overlaid bones actually animate.
    /// </summary>
    public static JObject BuildOverlayDestination(JObject originBones, JObject overlayBones)
    {
        var merged = (JObject)originBones.DeepClone();
        foreach (var prop in overlayBones.Properties())
            if (prop.Value is JObject overlayBone)
                merged[prop.Name] = (JObject)overlayBone.DeepClone();
        return merged;
    }

    // ── Bone write ────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes Translation, Rotation and Scale into <paramref name="bones"/>[<paramref name="boneName"/>].
    /// Creates the entry with default C+ metadata when absent; on an existing entry only
    /// the three transform channels are touched — ChildScaling and propagation flags are preserved.
    /// </summary>
    public static void SetBoneTransform(JObject bones, string boneName,
        Vector3 translation, Vector3 rotation, Vector3 scale)
    {
        if (bones[boneName] is not JObject bone)
        {
            bones[boneName] = new JObject
            {
                ["Translation"]           = Vec3Json(translation),
                ["Rotation"]              = Vec3Json(rotation),
                ["Scaling"]               = Vec3Json(scale),
                ["ChildScaling"]          = Vec3Json(Vector3.One),
                ["ChildScaleIndependent"] = false, // IPC schema name (see SetLinkedChildScaling)
                ["PropagateTranslation"]  = false,
                ["PropagateRotation"]     = false,
                ["PropagateScale"]        = false
            };
            return;
        }

        WriteChannel(bone, "Translation", translation, 0f);
        WriteChannel(bone, "Rotation",    rotation,    0f);
        WriteChannel(bone, "Scaling",     scale,       1f);
    }

    /// <summary>
    /// Expresses a Customize+ "linked" parent's child-scale magnitude explicitly so it survives the
    /// SetTemporaryProfile round-trip and the per-frame morph.
    ///
    /// In linked mode (<c>ChildScalingIndependent == false</c>, <c>PropagateScale == true</c>) C+
    /// does NOT serialise ChildScaling (<c>ShouldSerializeChildScaling() =&gt; ChildScalingIndependent</c>)
    /// and derives the child delta from the bone's *live* animated scale. That live derivation
    /// collapses to a no-op under BodyFlux's per-frame temporary-profile applies, so the parent
    /// scales but its children (e.g. toes under a foot) do not follow.
    ///
    /// Switching the bone to an *independent* child scale makes C+ compute the child delta as
    /// <c>(initialScale × ChildScaling) / initialScale</c> = <c>ChildScaling</c> — exact and
    /// independent of the live pose — reproducing the linked look. PropagateScale is set here so
    /// C+'s propagation gate stays open.
    ///
    /// <paramref name="scale"/> is the EXTRA factor applied on top of each child's own scale, not the
    /// parent's own scale. Callers ramp it between the two endpoints' child factors: at each end the
    /// factor is that end's own parent scale when the parent is linked there, or identity when it is
    /// not (children already carry their own explicit scale, so propagation must stay neutral to
    /// avoid doubling up and popping). Ramping from identity at BOTH ends — the old behaviour — is
    /// only correct when the origin is unlinked; when the origin was itself a linked parent (the
    /// normal C+ rig for fingers/toes) it forces the propagated children to their un-propagated
    /// vanilla size on the first frame, which is the start-of-morph "shrink to vanilla" artefact.
    /// </summary>
    public static void SetLinkedChildScaling(JObject bones, string boneName, Vector3 scale)
    {
        if (bones[boneName] is not JObject bone) return;

        // PropagateScale MUST be set true here, not merely inherited from the cloned template: C+'s
        // apply gate is `doPropagate = PropagateTranslation || PropagateRotation || PropagateScale`,
        // and a bone that fails it never propagates to children at all. GetProfile does not reliably
        // carry PropagateScale on the cloned (destination) entry, so we assert it explicitly.
        bone["PropagateScale"] = true;

        // NOTE: the field is "ChildScaleIndependent" (no "-ing", "Scale" not "Scaling"). That is the
        // exact name Customize+'s IPC schema (IPCBoneTransform) deserialises; the differently-spelled
        // "ChildScalingIndependent" used inside C+'s own profile format is silently ignored over IPC.
        bone["ChildScaleIndependent"] = true;
        bone["ChildScaling"]          = Vec3Json(scale); // written unconditionally: an independent
                                                          // bone must carry a valid ChildScaling.
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void WriteChannel(JObject bone, string key, Vector3 val, float defaultVal)
    {
        if (bone[key] is JObject ch)
        {
            ch["X"] = val.X;
            ch["Y"] = val.Y;
            ch["Z"] = val.Z;
        }
        else if (val != new Vector3(defaultVal))
        {
            // Channel absent on an existing bone — add it only when it carries a non-default value
            bone[key] = Vec3Json(val);
        }
    }
}
