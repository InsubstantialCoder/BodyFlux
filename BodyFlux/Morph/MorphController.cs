using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BodyFlux.Morph;

/// <summary>
/// Self-contained animation state machine for a single morph transition.
/// Knows nothing about IPC, rendering, or networking — it only interpolates
/// bone transforms and serialises the result to a Customize+ profile JSON string.
///
/// Typical call flow:
///   1. <see cref="Start"/> — supply origin/dest bone data, a working-profile template, and a mode.
///   2. Each game frame: call <see cref="Tick"/> → apply the returned JSON via C+ IPC.
///   3. <see cref="Stop"/> (or let the mode's natural end condition trigger) to finish.
/// </summary>
public sealed class MorphController
{
    // ── Internal animation record ─────────────────────────────────────────────

    private readonly record struct BoneAnim(
        Vector3 StartTranslation, Vector3 EndTranslation,
        Vector3 StartRotation,    Vector3 EndRotation,
        Vector3 StartScale,       Vector3 EndScale,
        // True when the bone drives its children at EITHER endpoint — a linked ("propagate scale")
        // parent OR an independent-child-scaling bone — so its child magnitude must be driven
        // explicitly (see BoneJsonHelper.SetLinkedChildScaling).
        bool    AnyLinked,
        // The extra child-scale factor to propagate at each endpoint: the parent's own scale when
        // linked there, else identity. Ramped between the two so propagated children (fingers/toes)
        // hold the origin's size at t=0 instead of snapping to their un-propagated vanilla size.
        Vector3 StartChildScale,  Vector3 EndChildScale,
        // "Chain" (propagate translation/rotation) at EITHER endpoint. Re-asserted onto the working
        // bone because the flag is otherwise lost through the profile round-trip (confirmed by JSON
        // dump: BodyFlux was emitting PropagateRotation:false). See BoneJsonHelper.SetPropagateFlags.
        bool    PropagateTranslation, bool PropagateRotation);

    // ── State ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The skeleton root bone. When <see cref="_externaliseRoot"/> is set, this bone's scale is
    /// driven through Brio's model transform instead of the Customize+ profile, so it does not
    /// fight Brio's per-frame pose-hold over the same bone (the cause of the GPose height flicker).
    /// </summary>
    private const string RootBone = "n_root";

    private bool      _isMorphing;
    private float     _progress;
    private float     _direction; // +1 = forward (origin→dest), -1 = reverse (dest→origin)
    private MorphMode  _mode;
    private EasingMode _easing;
    private JObject?  _workingProfile;
    private Dictionary<string, BoneAnim> _boneAnims = [];

    // When true, the root bone is omitted from the serialised C+ profile and its interpolated
    // scale is exposed via <see cref="CurrentRootScale"/> for the caller to drive externally (Brio).
    private bool _externaliseRoot;

    // When true, the morph is sweeping back to the origin for a Reset; it stops at progress 0
    // regardless of mode (so even LoopInfinite ends instead of bouncing).
    private bool _resetting;

    // ── Public read-only state ────────────────────────────────────────────────

    public bool       IsMorphing => _isMorphing;

    /// <summary>True while a Reset's reverse-to-origin sweep is in progress.</summary>
    public bool       IsResetting => _resetting;
    public float      Progress   => _progress;
    public int        BoneCount  => _boneAnims.Count;
    public MorphMode  Mode       => _mode;
    public EasingMode Easing     => _easing;

    /// <summary>
    /// When the root bone is externalised (GPose posing), the current interpolated scale of the
    /// root bone — to be applied to the actor through Brio's model transform. <c>null</c> when the
    /// root is not externalised or the morph does not animate the root.
    /// </summary>
    public Vector3? CurrentRootScale { get; private set; }

    /// <summary>True while a morph is partially complete and not currently ticking.</summary>
    public bool IsPaused => !_isMorphing && _boneAnims.Count > 0 && _progress > 0f && _progress < 1f;

    /// <summary>True when a morph has run to a boundary (progress 0 or 1) and stopped.</summary>
    public bool IsFinished => !_isMorphing && _boneAnims.Count > 0 && !IsPaused;

    /// <summary>The ObjectTable index this morph was started for.</summary>
    public ushort TargetIndex { get; private set; }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the per-bone animation plan and starts the morph ticker.
    /// </summary>
    /// <param name="targetIndex">ObjectTable index to target each tick (written to <see cref="TargetIndex"/>).</param>
    /// <param name="profileBase">
    ///   Full profile JObject used as the mutable working document.
    ///   Its "Bones" node is replaced in-place each tick, so pass a pre-cloned object
    ///   when you need to keep the original.
    /// </param>
    /// <param name="originBones">The "Bones" JObject from the origin (start) profile.</param>
    /// <param name="destBones">The "Bones" JObject from the destination (end) profile.</param>
    /// <param name="mode">Controls whether the animation plays once, loops once, or loops forever.</param>
    /// <param name="externaliseRoot">
    ///   When true (GPose posing), the root bone is omitted from the serialised C+ profile and its
    ///   interpolated scale is exposed via <see cref="CurrentRootScale"/> so the caller can drive it
    ///   through Brio's model transform instead — avoiding the C+/Brio fight over the root bone.
    /// </param>
    public void Start(ushort targetIndex,
                      JObject profileBase, JObject originBones, JObject destBones,
                      MorphMode mode = MorphMode.Simple, EasingMode easing = EasingMode.Linear,
                      bool externaliseRoot = false)
    {
        TargetIndex = targetIndex;
        _mode       = mode;
        _easing     = easing;
        _direction  = 1f;
        _externaliseRoot = externaliseRoot;
        CurrentRootScale = null;

        // Build animation plan: union of every bone in either profile.
        // Bones absent from a profile default to identity (T=0, R=0, S=1).
        var allBones = originBones.Properties().Select(p => p.Name)
            .Union(destBones.Properties().Select(p => p.Name))
            .ToHashSet();

        _boneAnims.Clear();
        foreach (var bone in allBones)
        {
            var (startT, startR, startS) = BoneJsonHelper.ReadBoneTransform(originBones, bone);
            var (destT,  destR,  destS)  = BoneJsonHelper.ReadBoneTransform(destBones,   bone);

            // Child-scale state is read per-endpoint, NOT with destination precedence: a bone can
            // drive its children in the origin, the destination, or both, and each end contributes
            // its own child-scale factor. Ramping the propagated child magnitude between those two
            // ends is what stops fingers/toes from collapsing to their un-propagated vanilla size at
            // the start of a morph between two linked rigs (the "shrink to vanilla" artefact).
            //
            // Two distinct Customize+ modes both drive children, and they must NOT be conflated:
            //   • Linked (PropagateScale, not independent): children follow the parent's own scale,
            //     so the child factor at that end is the parent scale (startS/destS).
            //   • Independent child scaling (ChildScaleIndependent): children carry an explicit
            //     ChildScaling vector the user chose (e.g. waist 1.02 while the parent is 1.15), so
            //     that value must be used verbatim — deriving it from the parent scale is the bug
            //     this handling fixes. Independent takes priority when a bone sets both flags.
            // Handles the mixed cases too: linked→unlinked lands the children on their own explicit
            // destination scale, unlinked→linked reproduces the original dest-only fix.
            bool originIndep  = BoneJsonHelper.IsChildScaleIndependent(originBones, bone);
            bool destIndep    = BoneJsonHelper.IsChildScaleIndependent(destBones,   bone);
            bool originLinked = BoneJsonHelper.IsBoneLinked(originBones, bone);
            bool destLinked   = BoneJsonHelper.IsBoneLinked(destBones,   bone);

            Vector3 startChildScale = originIndep  ? BoneJsonHelper.ReadChildScaling(originBones, bone)
                                    : originLinked ? startS
                                    : Vector3.One;
            Vector3 endChildScale   = destIndep    ? BoneJsonHelper.ReadChildScaling(destBones, bone)
                                    : destLinked   ? destS
                                    : Vector3.One;

            // "Chain" translation/rotation, read per endpoint. Kept on for the whole morph if either
            // end chains, so children track the parent as its move/rotation ramps in. C+ derives the
            // child delta from the bone's own transform, so an all-identity end simply won't propagate.
            bool propT = BoneJsonHelper.IsPropagateTranslation(originBones, bone)
                      || BoneJsonHelper.IsPropagateTranslation(destBones, bone);
            bool propR = BoneJsonHelper.IsPropagateRotation(originBones, bone)
                      || BoneJsonHelper.IsPropagateRotation(destBones, bone);

            _boneAnims[bone] = new BoneAnim(
                StartTranslation: startT, EndTranslation: destT,
                StartRotation:    startR, EndRotation:    destR,
                StartScale:       startS, EndScale:       destS,
                AnyLinked:        originIndep || destIndep || originLinked || destLinked,
                // Child factor at each end: the bone's explicit ChildScaling when it uses independent
                // child scaling; else the parent's own scale when linked (C+ propagates the parent's
                // scale to its children); else identity so an end whose children carry their own
                // explicit scale is not doubled up. See SetLinkedChildScaling.
                StartChildScale:  startChildScale,
                EndChildScale:    endChildScale,
                PropagateTranslation: propT, PropagateRotation: propR);
        }

        // Initialise the working document: ensure all animated bones exist at their
        // start values before the first tick so C+ sees a valid document immediately.
        //
        // IMPORTANT: when profileBase already carries a "Bones" node, we must reuse that exact
        // object — never re-assign it to itself. Newtonsoft clones a JToken that is assigned to a
        // property it already belongs to, so `_workingProfile["Bones"] = _workingProfile["Bones"]`
        // would leave the local reference pointing at an orphaned copy while the document keeps a
        // now-empty clone. Every seed below (CloneBoneTemplate, SetLinkedChildScaling,
        // SetPropagateFlags) would then write to the orphan and never reach C+, so the morph would
        // fall back to Tick creating bare bones each frame — dropping all propagation flags. Only
        // create-and-attach a fresh node when one is genuinely absent.
        _workingProfile = profileBase;
        if (_workingProfile["Bones"] is not JObject workBones)
        {
            workBones = new JObject();
            _workingProfile["Bones"] = workBones;
        }

        foreach (var (bone, anim) in _boneAnims)
        {
            // When externalising the root, never write it into the C+ profile — Brio owns it.
            if (_externaliseRoot && bone == RootBone) continue;

            // Seed the working bone from the source entry so all C+ metadata (PropagateScale/
            // Rotation/Translation, ChildScalingIndependent, etc.) is preserved; SetBoneTransform
            // then only animates T/R/S on top via its existing-bone (WriteChannel) path.
            // Endpoint choice: prefer the destination entry, fall back to origin. A linked flag
            // (e.g. PropagateScale) is a boolean and can't be interpolated, so the morph ends in
            // the destination's link state — the intuitive result when morphing *toward* a profile.
            // Do not "simplify" this to origin-first.
            var template = BoneJsonHelper.CloneBoneTemplate(originBones, destBones, bone);
            if (template != null)
                workBones[bone] = template;

            BoneJsonHelper.SetBoneTransform(workBones, bone,
                anim.StartTranslation, anim.StartRotation, anim.StartScale);

            // Linked ("propagate scale") parents need their child magnitude expressed explicitly —
            // see SetLinkedChildScaling. Seed it at the origin's child factor (the parent's own
            // origin scale when linked there, else identity), so a rig that already propagated to
            // its children at the origin keeps that size on the first frame instead of snapping the
            // children to vanilla. It then ramps toward the destination factor in Tick.
            if (anim.AnyLinked)
                BoneJsonHelper.SetLinkedChildScaling(workBones, bone, anim.StartChildScale);

            // Re-assert the translation/rotation "chain" flags so children (eyes, hair, cheeks …)
            // follow the parent's move/rotation. The flag is lost through the working-document build
            // (dump confirmed PropagateRotation:false was reaching C+). Constant for the whole morph,
            // so set once here — Tick's WriteChannel path leaves the flags intact.
            if (anim.PropagateTranslation || anim.PropagateRotation)
                BoneJsonHelper.SetPropagateFlags(workBones, bone,
                    anim.PropagateTranslation, anim.PropagateRotation);
        }

        // Seed the externalised root scale at its start value so the very first tick is correct.
        if (_externaliseRoot && _boneAnims.TryGetValue(RootBone, out var rootAnim))
        {
            CurrentRootScale = rootAnim.StartScale;
            workBones.Remove(RootBone); // defensive: ensure it never reaches C+ via the template
        }

        // Remove channels that are identity in both origin and destination.
        // Sending identity Translation/Rotation to C+ via SetTempProfile would override
        // any external positioning on that bone (e.g. Brio moving n_root to reposition
        // the actor in the scene). Channels absent from the JSON are left untouched by C+.
        foreach (var (boneName, anim) in _boneAnims)
        {
            if (workBones[boneName] is not JObject boneNode) continue;
            if (anim.StartTranslation == Vector3.Zero && anim.EndTranslation == Vector3.Zero)
                boneNode.Remove("Translation");
            if (anim.StartRotation == Vector3.Zero && anim.EndRotation == Vector3.Zero)
                boneNode.Remove("Rotation");
        }

        _progress  = 0f;
        _isMorphing = true;
    }

    /// <summary>Suspends the ticker without discarding animation state.</summary>
    public void Pause() => _isMorphing = false;

    /// <summary>Resumes a previously paused morph from exactly where it stopped.</summary>
    public void Resume()
    {
        if (IsPaused) _isMorphing = true;
    }

    /// <summary>Flips the travel direction mid-morph, while paused, or after finishing.</summary>
    public void Reverse()
    {
        if (_isMorphing || IsPaused)
            _direction *= -1f;
        else if (_boneAnims.Count > 0) // finished at a boundary — restart in opposite direction
        {
            _direction  *= -1f;
            _isMorphing  = true;
        }
    }

    /// <summary>
    /// Begins a reverse sweep back to the origin (progress 0) for a Reset, stopping on arrival
    /// regardless of mode. Animating every bone — including n_root — back through non-identity
    /// values lets Customize+ apply them frame by frame; a one-shot jump straight to identity is
    /// skipped by C+, which is why a plain reset leaves the root stuck while a Reverse does not.
    /// </summary>
    public void BeginReset()
    {
        if (_boneAnims.Count == 0) return;
        _resetting  = true;
        _direction  = -1f;
        _isMorphing = true;
    }

    /// <summary>Stops the morph and clears all state.</summary>
    public void Stop()
    {
        _isMorphing      = false;
        _progress        = 0f;
        _direction       = 1f;
        _easing          = EasingMode.Linear;
        _boneAnims       = [];
        _workingProfile  = null;
        _externaliseRoot = false;
        _resetting       = false;
        CurrentRootScale = null;
    }

    // ── Per-frame update ──────────────────────────────────────────────────────

    /// <summary>
    /// Advances the morph by <paramref name="seconds"/> seconds at <paramref name="speed"/>
    /// (progress fraction per second), updates the working profile, and returns the
    /// serialised JSON string ready to be pushed to Customize+ via IPC.
    ///
    /// The direction of travel depends on <see cref="Mode"/>:
    /// <list type="bullet">
    ///   <item><see cref="MorphMode.Simple"/> — forward only, stops at 1.</item>
    ///   <item><see cref="MorphMode.LoopSingle"/> — forward then reverse; stops when back at 0.</item>
    ///   <item><see cref="MorphMode.LoopInfinite"/> — ping-pongs indefinitely until <see cref="Stop"/>.</item>
    /// </list>
    /// Returns <c>null</c> when the morph is not currently running (paused, stopped, or not started).
    /// </summary>
    public string? Tick(float seconds, float speed)
    {
        if (!_isMorphing || _workingProfile == null) return null;

        _progress += _direction * speed * seconds;

        // ── Boundary handling ─────────────────────────────────────────────────
        if (_direction > 0f && _progress >= 1f)
        {
            _progress = 1f;
            if (_mode == MorphMode.Simple)
                _isMorphing = false;      // Simple: stop at destination
            else
                _direction = -1f;        // Loop modes: reverse back to origin
        }
        else if (_direction < 0f && _progress <= 0f)
        {
            _progress = 0f;
            if (_mode == MorphMode.LoopInfinite && !_resetting)
                _direction = 1f;         // Infinite: immediately go forward again
            else
                _isMorphing = false;      // LoopSingle / Reset sweep: stop at origin
        }

        // ── Bone interpolation ────────────────────────────────────────────────
        var workBones = _workingProfile["Bones"] as JObject;
        if (workBones == null) return null;

        var t = EasingHelper.Apply(_progress, _easing);
        foreach (var (bone, anim) in _boneAnims)
        {
            // The externalised root is driven through Brio, not C+ — skip it in the profile
            // but still compute its interpolated scale for the caller to apply.
            if (_externaliseRoot && bone == RootBone)
            {
                CurrentRootScale = Vector3.Lerp(anim.StartScale, anim.EndScale, t);
                continue;
            }

            var scale = Vector3.Lerp(anim.StartScale, anim.EndScale, t);
            BoneJsonHelper.SetBoneTransform(workBones, bone,
                translation: Vector3.Lerp(anim.StartTranslation, anim.EndTranslation, t),
                rotation:    Vector3.Lerp(anim.StartRotation,    anim.EndRotation,    t),
                scale:       scale);

            // Drive the linked parent's child propagation so its children (e.g. toes under a scaled
            // foot) follow. ChildScaling is the *extra* factor applied on top of each child's own
            // scale, so it ramps between the two endpoints' child factors (StartChildScale →
            // EndChildScale). Each factor is the parent's own scale when linked at that end, or
            // identity when unlinked there — an unlinked end stays neutral so a child that carries
            // its own explicit scale is not doubled up. See BoneJsonHelper.SetLinkedChildScaling.
            if (anim.AnyLinked)
                BoneJsonHelper.SetLinkedChildScaling(workBones, bone,
                    Vector3.Lerp(anim.StartChildScale, anim.EndChildScale, t));
        }

        return _workingProfile.ToString(Formatting.None);
    }
}
