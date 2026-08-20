using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;
using BodyFlux.Ipc;
using BodyFlux.Io;
using BodyFlux.Morph;

namespace BodyFlux.Services;

/// <summary>
/// Owns all morph session state and behaviour: starting, ticking, pausing, reversing, and
/// resetting player and Brio morphs; sequence playback; preset apply/save/clear; and MCDF
/// origin management. Depends on the IPC layers and SyncManager but not on Plugin, so it can
/// be created, ticked, and tested independently. Plugin calls <see cref="Tick"/> once per
/// frame after <c>SyncManager.ProcessIncomingFrames</c>.
/// </summary>
public sealed class MorphEngine
{
    private readonly CustomizePlusIpc                  _ipc;
    private readonly BrioIpc                           _brioIpc;
    private readonly SyncManager                       _sync;
    private readonly IObjectTable                      _objects;
    private readonly IClientState                      _clientState;
    private readonly GaugeReader                       _gauges;
    private readonly Configuration                     _config;
    private readonly IChatGui                          _chat;
    private readonly IPluginLog                        _log;

    private readonly MorphSession                      _player = new();
    private readonly Dictionary<ushort, MorphSession>  _brio   = [];

    // True pre-morph model scale per externalised actor, kept across chained morphs (so starting a
    // new morph before resetting does not capture the previous morph's inflated scale as "original").
    // Cleared when the actor's model scale is restored on reset.
    private readonly Dictionary<ushort, Vector3>       _externalBaseScale = [];

    private string? _brioOriginJson;

    // ── Public state ──────────────────────────────────────────────────────────

    public string BrioOriginLabel  { get; private set; } = "";
    public bool   BrioOriginLoaded => _brioOriginJson != null;

    // UI-facing selection state
    public int     SelectedProfileIndex      { get; set; } = -1;
    public int     SelectedTemplateIndex     { get; set; } = -1;
    public int     SelectedBrioActorIndex    { get; set; } = -1;
    public int     SelectedBrioDestIndex     { get; set; } = -1;
    public int     SelectedBrioTemplateIndex { get; set; } = -1;
    public string? TargetPlayerName          { get; set; }

    // Player session projections (drive the Player tab)
    public bool       IsMorphing        => _player.Controller.IsMorphing;
    public float      Progress          => _player.Controller.Progress;
    public int        BoneAnimCount     => _player.Controller.BoneCount;
    public bool       IsPaused          => _player.Controller.IsPaused;
    public MorphMode  CurrentMorphMode  => _player.Controller.Mode;
    public EasingMode CurrentEasingMode => _player.Controller.Easing;
    public ushort     MorphTargetIndex  => _player.Controller.TargetIndex;
    public bool       IsSequenceRunning => _player.SeqRunning;
    public int        SequenceStep      => _player.SeqRunning ? _player.SeqIndex + 1 : 0;
    public int        SequenceStepCount => _player.SeqSteps?.Count ?? 0;

    /// <summary>
    /// True when a targeted morph can correctly restore the target on Reset — i.e. the peer
    /// has already shared its base scaling. Always true for self-morphs (no target).
    /// </summary>
    public bool TargetBaseReady
    {
        get
        {
            if (string.IsNullOrEmpty(TargetPlayerName)) return true;
            return _sync.TryGetPeerBaseProfile(TargetPlayerName, out var j)
                && !string.IsNullOrEmpty(j);
        }
    }

    // ── Brio actor state ──────────────────────────────────────────────────────

    /// <summary>Snapshot of one active Brio actor morph for rendering the active-morphs list.</summary>
    public readonly record struct BrioMorphState(
        ushort Index, float Progress, int BoneCount,
        bool IsMorphing, bool IsPaused, bool IsFinished, bool SeqRunning, int SeqStep, int SeqStepCount);

    public bool  IsBrioActorActive(ushort index)   => _brio.ContainsKey(index);
    public float GetBrioActorProgress(ushort index) =>
        _brio.TryGetValue(index, out var s) ? s.Controller.Progress : 0f;

    public List<BrioMorphState> GetActiveBrioMorphs()
    {
        var list = new List<BrioMorphState>(_brio.Count);
        foreach (var (idx, s) in _brio)
            list.Add(new BrioMorphState(
                idx, s.Controller.Progress, s.Controller.BoneCount,
                s.Controller.IsMorphing, s.Controller.IsPaused, s.Controller.IsFinished,
                s.SeqRunning, s.SeqRunning ? s.SeqIndex + 1 : 0, s.SeqSteps?.Count ?? 0));
        list.Sort((a, b) => a.Index.CompareTo(b.Index));
        return list;
    }

    // ── Construction ──────────────────────────────────────────────────────────

    public MorphEngine(
        CustomizePlusIpc ipc, BrioIpc brioIpc, SyncManager sync,
        IObjectTable objects, IClientState clientState, IJobGauges gauges,
        Configuration config, IChatGui chat, IPluginLog log)
    {
        _ipc         = ipc;
        _brioIpc     = brioIpc;
        _sync        = sync;
        _objects     = objects;
        _clientState = clientState;
        _gauges      = new GaugeReader(gauges, objects);
        _config      = config;
        _chat        = chat;
        _log         = log;
    }

    // ── Brio actor query ──────────────────────────────────────────────────────

    public List<(ushort Index, string Name)> GetGPoseActors() => _brioIpc.GetManagedActors();

    // ── Framework tick ────────────────────────────────────────────────────────

    /// <summary>
    /// Advances all active sessions by <paramref name="seconds"/>. Called once per framework
    /// update, after SyncManager has processed any incoming peer frames.
    /// </summary>
    /// <summary>Progress-per-second of the animated Reset sweep back to the origin (~0.5 s full range).</summary>
    private const float ResetSweepSpeed = 2.0f;

    /// <summary>Smoothing rate for gauge-driven morphs (higher = snappier; ~6 eases nicely).</summary>
    private const float GaugeSmoothing = 6f;

    public void Tick(float seconds)
    {
        if (_player.GaugeDriver is { } pres)
            TickGaugeSession(_player, seconds, pres, broadcast: true);
        else if (_player.Controller.IsMorphing)
            TickSession(_player, seconds,
                _player.Resetting ? ResetSweepSpeed : _config.GrowthSpeed, broadcast: true);

        // The animated Reset sweep has reached the origin — finalise the teardown now (on the
        // framework thread). n_root has already been swept back through non-identity values, so the
        // DeleteTempProfile in ResetSession leaves it at the origin instead of stuck.
        if (_player.Resetting && !_player.Controller.IsMorphing)
        {
            _player.Resetting = false;
            ResetSession(_player, isPlayer: true);
        }

        // For a completed remote-target morph (progress hit 1.0, IsMorphing stopped), keep
        // re-applying the final morphed profile each tick. The SyncManager's peer-interp is
        // continuously fed the target's own base-profile broadcasts and would otherwise
        // overwrite the temp profile and snap the target back to unmorhped on the applier's screen.
        if (!_player.Controller.IsMorphing && !_player.Resetting
            && _player.NetworkTargetName != null && _player.CompletedRemoteJson != null)
            _ipc.SetTempProfile(_player.TargetIndex, _player.CompletedRemoteJson);

        if (_brio.Count > 0)
            foreach (var s in _brio.Values)
                // Brio actors can't be gauge-driven (no gauge memory for other actors), but keep the
                // shape parallel in case that ever changes.
                if (s.GaugeDriver is { } bres)
                    TickGaugeSession(s, seconds, bres, broadcast: false);
                else if (s.Controller.IsMorphing)
                    TickSession(s, seconds, _config.BrioGrowthSpeed, broadcast: false);
    }

    /// <summary>
    /// Drives one gauge-linked session from its live resource: reads the fill, applies invert,
    /// exponentially smooths it (frame-rate independent), and pushes the resulting profile to C+
    /// (and to peers when broadcasting). A null reading (wrong job / logged out) holds the body
    /// steady rather than snapping to 0.
    /// </summary>
    private void TickGaugeSession(MorphSession s, float seconds, GaugeResource res, bool broadcast)
    {
        var live = _gauges.Read(res);
        if (live is not { } fill) return;          // wrong job / logged out — hold steady

        if (s.GaugeInvert) fill = 1f - fill;

        // Exponential smoothing toward the live reading (frame-rate independent).
        float k = 1f - MathF.Exp(-GaugeSmoothing * seconds);
        s.SmoothedFill += (fill - s.SmoothedFill) * k;

        var json = s.Controller.TickDriven(s.SmoothedFill);
        if (json == null) return;

        var (errorCode, _) = _ipc.SetTempProfile(s.Controller.TargetIndex, json);
        if (errorCode == 99)
        {
            _log.Error("[BodyFlux] Customize+ IPC lost during gauge morph. Stopping.");
            StopGaugeMorph();
            return;
        }

        if (s.RootExternalised && s.Controller.CurrentRootScale is { } rootScale)
            ApplyExternalRootScale(s, rootScale);

        if (broadcast && (s.Controller.TargetIndex == 0 || s.NetworkTargetName != null))
            _sync.TickBroadcast(json, s.NetworkTargetName, s.Controller.Progress, seconds);
    }

    private void TickSession(MorphSession s, float seconds, float baseSpeed, bool broadcast)
    {
        var json = s.Controller.Tick(seconds, s.SpeedOverride ?? baseSpeed);
        if (json == null) return;

        var (errorCode, _) = _ipc.SetTempProfile(s.Controller.TargetIndex, json);
        if (errorCode == 99)
        {
            _log.Error("[BodyFlux] Customize+ IPC lost during morph. Stopping.");
            s.Controller.Stop();
            return;
        }

        // GPose root externalisation: drive the root bone's interpolated scale through Brio's
        // model transform (instead of C+) so it does not fight Brio's pose-hold over n_root.
        if (s.RootExternalised && s.Controller.CurrentRootScale is { } rootScale)
            ApplyExternalRootScale(s, rootScale);

        if (broadcast && (s.Controller.TargetIndex == 0 || s.NetworkTargetName != null))
            _sync.TickBroadcast(json, s.NetworkTargetName, s.Controller.Progress, seconds);

        // Cache final frame for remote-target Simple morphs so the engine can re-apply it after
        // the morph stops. Without this the SyncManager's peer-interp (driven by the target's own
        // base-profile broadcasts) would overwrite the temp profile and snap them back on
        // the applier's screen the moment TickSession stops being called.
        if (s.NetworkTargetName != null && !s.Controller.IsMorphing && s.Controller.Progress >= 1f)
            s.CompletedRemoteJson = json;

        if (s.SeqRunning && !s.Controller.IsMorphing && s.Controller.Progress >= 1f)
            AdvanceSequence(s);
    }

    /// <summary>
    /// Drives the actor's whole-model scale through Brio to reproduce the morph's root-bone scale,
    /// leaving position/rotation untouched so the user's manual repositioning is preserved.
    /// </summary>
    private void ApplyExternalRootScale(MorphSession s, Vector3 rootScale)
    {
        if (s.ModelTransformIndex is not { } idx) return;
        var actor = _objects[idx];
        if (actor == null) return;

        var baseScale = s.OriginalModelScale ?? Vector3.One;
        var target    = baseScale * rootScale; // component-wise; base is normally (1,1,1)
        _brioIpc.SetModelTransform(actor, position: null, rotation: null, scale: target);
    }

    // ── Player morph controls ─────────────────────────────────────────────────

    /// <summary>
    /// Reads the active C+ profile (origin) and the selected profile (destination), then
    /// hands them to <see cref="MorphController"/> to start the transition. Optional overrides
    /// let callers (presets, history) supply per-morph values without touching saved config.
    /// </summary>
    public void StartGrowth(float? speedOverride = null, MorphMode? modeOverride = null, EasingMode? easingOverride = null,
                            MorphTargetMode? targetModeOverride = null, Guid? originOverride = null,
                            Guid? easingCurveIdOverride = null)
    {
        if (_player.Controller.IsMorphing)
        {
            _chat.PrintError("[BodyFlux] A morph is already in progress — wait for it to finish or press Reset.");
            return;
        }
        if (_player.GaugeDriver != null) return; // a gauge morph owns the session — unbind it first
        _player.SpeedOverride = speedOverride;

        var resolvedTargetMode = targetModeOverride ?? _config.MorphTargetMode;

        var profiles  = _ipc.Profiles;
        var templates = _ipc.Templates;
        if (resolvedTargetMode == MorphTargetMode.TemplateOverlay)
        {
            if (SelectedTemplateIndex < 0 || SelectedTemplateIndex >= templates.Count)
            {
                _log.Warning("[BodyFlux] No overlay template selected.");
                return;
            }
        }
        else if (SelectedProfileIndex < 0 || SelectedProfileIndex >= profiles.Count)
        {
            _log.Warning("[BodyFlux] No destination profile selected.");
            return;
        }

        // ── Resolve target index ──────────────────────────────────────────────
        ushort targetIndex = 0;
        if (!string.IsNullOrEmpty(TargetPlayerName))
        {
            if (!_sync.IsConnected)
            {
                _chat.PrintError("[BodyFlux] Network Sync must be active to morph other players.");
                return;
            }
            if (!_sync.IsConsentingPeer(TargetPlayerName))
            {
                _chat.PrintError(
                    $"[BodyFlux] '{TargetPlayerName}' has not enabled remote morphing " +
                    "or is not connected with the same Sync Key.");
                return;
            }
            var found = FindCharacterIndex(TargetPlayerName);
            if (found == null)
            {
                _chat.PrintError($"[BodyFlux] Cannot find '{TargetPlayerName}' nearby.");
                return;
            }
            targetIndex = found.Value;
        }

        // ── Fetch origin (the target's current scaling) ───────────────────────
        string? originJson = null;
        if (!string.IsNullOrEmpty(TargetPlayerName))
        {
            if (_sync.TryGetPeerBaseProfile(TargetPlayerName, out var peerBase)
                && !string.IsNullOrEmpty(peerBase))
                originJson = peerBase;
            else
                _log.Warning($"[BodyFlux] '{TargetPlayerName}' hasn't shared their base scaling yet; " +
                             "morphing from identity. Their appearance may not restore correctly on Reset.");
        }
        else
        {
            originJson = ResolvePlayerOriginJson(originOverride);
            if (originJson == null)
            {
                _log.Error("[BodyFlux] No active C+ profile on the local player. " +
                           "Make sure a Customize+ profile is active.");
                return;
            }
        }

        // ── Fetch destination ─────────────────────────────────────────────────
        Guid    destId;
        string  destName;
        Guid?   templateOwnerProfileId = null;
        int     ec3;
        string? destJson;
        if (resolvedTargetMode == MorphTargetMode.TemplateOverlay)
        {
            var t = templates[SelectedTemplateIndex];
            destId                 = t.Id;
            destName               = t.Name;
            templateOwnerProfileId = t.OwnerProfileId;
            (ec3, destJson)        = _ipc.GetTemplate(t.OwnerProfileId, t.Id);
        }
        else
        {
            destId          = profiles[SelectedProfileIndex].Id;
            destName        = profiles[SelectedProfileIndex].Name;
            (ec3, destJson) = _ipc.GetProfile(destId);
        }
        if (ec3 != 0 || destJson == null)
        {
            _log.Error($"[BodyFlux] Fetching morph destination failed (ec={ec3}).");
            return;
        }

        // ── Parse both ends before touching the session ───────────────────────
        // Parsing first means a bad profile aborts without leaving a half-configured session
        // behind, and TryParseProfile keeps the failure from escaping into the ImGui draw loop.
        if (!TryParseProfile(originJson, "origin",      out var originJObj)) return;
        if (!TryParseProfile(destJson,   "destination", out var destJObj))   return;

        // ── Configure the player session ──────────────────────────────────────
        _player.OriginProfileJson = originJson;
        _player.NetworkTargetName = targetIndex == 0 ? null : TargetPlayerName;
        _player.TargetIndex       = targetIndex;
        _player.SeqSteps          = null;
        _player.SeqRunning        = false;

        var originBones = originJObj["Bones"] as JObject ?? new JObject();
        var destBones   = destJObj  ["Bones"] as JObject ?? new JObject();

        var resolvedMode     = modeOverride   ?? _config.MorphMode;
        var resolvedEasing   = easingOverride ?? _config.EasingMode;
        var resolvedCurveId  = easingCurveIdOverride ?? _config.EasingCurveId;
        var resolvedCurve    = resolvedCurveId is { } cid ? _config.CustomEasingCurves.Find(c => c.Id == cid) : null;

        // Template Overlay: only the bones defined in the selected template should animate —
        // everything else stays at the origin's value. Build a virtual destination that reflects
        // that instead of changing MorphController's origin/destination interpolation.
        var effectiveDestBones = resolvedTargetMode == MorphTargetMode.TemplateOverlay
            ? BoneJsonHelper.BuildOverlayDestination(originBones, destBones)
            : destBones;

        // In GPose, drive the root bone through Brio's model transform instead of C+ so it does
        // not fight Brio's pose-hold over n_root (the cause of the height flicker).
        TrySetupRootExternalisation(_player, targetIndex);
        _player.Controller.Start(targetIndex, originJObj, originBones, effectiveDestBones,
                                 resolvedMode, resolvedEasing, _player.RootExternalised, resolvedCurve);

        _sync.ResetSendThrottle();

        // The target's consent covered this morph and no more: spend it now that the morph is
        // committed, so applying another one to them prompts them again.
        if (targetIndex != 0 && !string.IsNullOrEmpty(TargetPlayerName))
            _sync.ConsumeConsent(TargetPlayerName);

        // Record in history (dedup by destination, newest first, cap at 5). Carries the origin that
        // was actually used (preset override, else the pin) so re-applying from history reproduces
        // the same morph instead of picking up whatever origin happens to be pinned later — see
        // MorphPreset.OriginProfileId.
        Guid? usedOriginId = string.IsNullOrEmpty(TargetPlayerName) ? (originOverride ?? _config.OriginProfileId) : null;
        var historyEntry = new MorphPreset(destId, destName,
            speedOverride ?? _config.GrowthSpeed, resolvedMode, resolvedEasing,
            TargetMode: resolvedTargetMode, TemplateOwnerProfileId: templateOwnerProfileId,
            OriginProfileId: usedOriginId,
            OriginProfileName: usedOriginId is { } uid ? ResolveProfileName(uid) : null,
            EasingCurveId: resolvedCurveId);
        _config.RecentMorphs.RemoveAll(h => h.ProfileId == destId);
        _config.RecentMorphs.Insert(0, historyEntry);
        if (_config.RecentMorphs.Count > 5)
            _config.RecentMorphs.RemoveAt(5);
        _config.Save();

        string targetLabel = string.IsNullOrEmpty(TargetPlayerName) ? "self" : $"'{TargetPlayerName}'";
        _log.Information($"[BodyFlux] Morphing {_player.Controller.BoneCount} bones on {targetLabel}: " +
                         $"→ '{destName}'");
    }

    /// <summary>
    /// Parses a Customize+ profile, reporting and aborting the morph instead of throwing.
    /// <see cref="StartGrowth"/> is called straight from the ImGui draw loop, so an exception here
    /// escapes into Dalamud's window host and takes the UI frame down with it — which is exactly
    /// what a peer's unreadable base scaling used to do.
    /// A null <paramref name="json"/> is not an error: it means "no origin", i.e. morph from identity.
    /// </summary>
    private bool TryParseProfile(string? json, string what, out JObject parsed)
    {
        parsed = new JObject();
        if (json == null) return true;
        try
        {
            parsed = JObject.Parse(json);
            return true;
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            _log.Error($"[BodyFlux] Unreadable {what} profile — morph aborted. {ex.Message}");
            _chat.PrintError($"[BodyFlux] The {what} profile could not be read — morph aborted.");
            return false;
        }
    }

    public void PauseGrowth()   => _player.Controller.Pause();
    public void ResumeGrowth()  => _player.Controller.Resume();
    public void ReverseGrowth() => _player.Controller.Reverse();
    /// <summary>
    /// Resets the local player's morph — or, with nothing in flight, just applies the requested
    /// state directly. <paramref name="toActiveProfile"/> selects the landing state: <c>false</c>
    /// ("Reset to Origin", default) lands on the morph's starting point (pinned Origin Profile if
    /// set, else whatever was live-active when the morph began). <c>true</c> ("Reset to Active")
    /// instead lands on whatever's really active on the character right now, ignoring the origin.
    /// Both work at any time, mid-morph or fully idle, and either can be pressed after the other.
    /// Outside GPose the root bone runs through Customize+, and a one-shot reset to identity is
    /// skipped by C+ (it ignores identity transforms), leaving n_root stuck. So a mid-morph reset
    /// still animates a quick reverse back to the origin — the path Reverse uses, which C+ applies
    /// frame by frame — and finalises once it arrives. In GPose the root is handled by Brio's model
    /// transform, so the instant teardown already restores correctly.
    /// </summary>
    public void ResetGrowth(bool toActiveProfile = false)
    {
        // A gauge morph is unbound (not time-reset): hand off to StopGaugeMorph, which clears the
        // driver and then calls back here to run the normal reverse sweep / restore.
        if (_player.GaugeDriver != null)
        {
            StopGaugeMorph();
            return;
        }

        bool morphInFlight = _player.Resetting || _player.Controller.BoneCount > 0;

        if (!morphInFlight)
        {
            // No session to animate through — resolve and apply the requested state directly.
            // Only ever touches the local player (index 0): with nothing in flight there is no
            // peer/Brio target of ours to touch, so this can never clobber someone else's overlay.
            if (toActiveProfile)
            {
                _ipc.DeleteTempProfile(0);
                _sync.SendStop(null);
            }
            else
            {
                var originJson = ResolvePlayerOriginJson();
                if (originJson == null)
                {
                    _log.Warning("[BodyFlux] Reset to Origin: no active C+ profile on the local player.");
                    return;
                }
                _ipc.SetTempProfile(0, originJson);
                _sync.SendFrame(originJson, null);
            }
            return;
        }

        _player.ResetToLiveActive = toActiveProfile;

        if (!_player.RootExternalised
            && !_player.Resetting
            && _player.Controller.Progress > 0.001f)
        {
            _player.SpeedOverride = null; // sweep uses ResetSweepSpeed, not any per-morph override
            _player.Resetting     = true;
            _player.Controller.BeginReset();
        }
        else
        {
            ResetSession(_player, isPlayer: true);
        }
    }

    // ── Gauge-linked morph ────────────────────────────────────────────────────

    /// <summary>True while a gauge-linked morph is bound on the local player.</summary>
    public bool IsGaugeMorphing => _player.GaugeDriver != null;

    /// <summary>Live 0–1 fill of a resource for the local player, or null when unreadable (UI preview).</summary>
    public float? ReadGaugeFill(GaugeResource resource) => _gauges.Read(resource);

    /// <summary>
    /// Binds the local player's morph to a live resource (MP or a bar-type job gauge).
    /// The morph then tracks the resource every frame until <see cref="StopGaugeMorph"/>.
    /// Self / local-player only. Mutually exclusive with a running/resting time or sequence morph.
    /// </summary>
    public void StartGaugeMorph(GaugeResource resource, bool invert)
    {
        // One driver per session, and never over a running or resting (un-reset) time morph —
        // a residual plan means a temp profile is still applied, and binding would snap.
        if (_player.GaugeDriver != null || _player.Controller.IsMorphing || _player.Controller.BoneCount > 0)
            return;

        var resolvedTargetMode = _config.MorphTargetMode;
        var profiles  = _ipc.Profiles;
        var templates = _ipc.Templates;
        if (resolvedTargetMode == MorphTargetMode.TemplateOverlay)
        {
            if (SelectedTemplateIndex < 0 || SelectedTemplateIndex >= templates.Count)
            {
                _log.Warning("[BodyFlux] No overlay template selected.");
                return;
            }
        }
        else if (SelectedProfileIndex < 0 || SelectedProfileIndex >= profiles.Count)
        {
            _log.Warning("[BodyFlux] No destination profile selected.");
            return;
        }

        // Gauge morphs are local-player only (gauge memory isn't exposed for other actors).
        // Resolve origin the same way StartGrowth does: a pinned Origin Profile wins, then the
        // live-active C+ profile, then a temp profile already applied by another plugin.
        string? originJson = ResolvePlayerOriginJson();
        if (originJson == null)
        {
            _log.Error("[BodyFlux] No active C+ profile on the local player.");
            return;
        }

        string? destJson;
        int     ec3;
        if (resolvedTargetMode == MorphTargetMode.TemplateOverlay)
        {
            var t = templates[SelectedTemplateIndex];
            (ec3, destJson) = _ipc.GetTemplate(t.OwnerProfileId, t.Id);
        }
        else
        {
            (ec3, destJson) = _ipc.GetProfile(profiles[SelectedProfileIndex].Id);
        }
        if (ec3 != 0 || destJson == null)
        {
            _log.Error($"[BodyFlux] Fetching gauge morph destination failed (ec={ec3}).");
            return;
        }

        _player.OriginProfileJson = originJson;
        _player.NetworkTargetName = null;
        _player.TargetIndex       = 0;
        _player.SeqSteps          = null;
        _player.SeqRunning        = false;
        _player.SpeedOverride     = null;

        var originJObj  = JObject.Parse(originJson);
        var destJObj    = JObject.Parse(destJson);
        var originBones = originJObj["Bones"] as JObject ?? new JObject();
        var destBones   = destJObj  ["Bones"] as JObject ?? new JObject();

        // Template Overlay: only the bones defined in the selected template should track the
        // gauge — everything else stays at the origin's value (same approach as StartGrowth).
        var effectiveDestBones = resolvedTargetMode == MorphTargetMode.TemplateOverlay
            ? BoneJsonHelper.BuildOverlayDestination(originBones, destBones)
            : destBones;

        TrySetupRootExternalisation(_player, 0);
        // Build the bone plan. Mode/easing: Simple + the configured easing. The time ticker never
        // runs for a gauge morph, but easing still shapes the gauge→scale curve.
        var gaugeCurve = _config.EasingCurveId is { } gcid ? _config.CustomEasingCurves.Find(c => c.Id == gcid) : null;
        _player.Controller.Start(0, originJObj, originBones, effectiveDestBones,
                                 MorphMode.Simple, _config.EasingMode, _player.RootExternalised, gaugeCurve);

        // Switch from time-driven to gauge-driven: clear the controller's morphing flag so the
        // engine's gauge branch owns it, and seed the smoother at the current reading so it does
        // not sweep from 0 on bind.
        _player.Controller.Pause();                 // _isMorphing = false; plan/_progress kept
        _player.GaugeDriver  = resource;
        _player.GaugeInvert  = invert;
        var seed = _gauges.Read(resource) ?? 0f;
        _player.SmoothedFill = invert ? 1f - seed : seed;

        string destName = resolvedTargetMode == MorphTargetMode.TemplateOverlay
            ? templates[SelectedTemplateIndex].Name
            : profiles[SelectedProfileIndex].Name;

        _sync.ResetSendThrottle();
        _log.Information($"[BodyFlux] Gauge morph bound: {GaugeReader.Label(resource)} " +
                        $"(invert={invert}) → '{destName}'.");
    }

    /// <summary>Unbinds the gauge driver and animates the body back to origin.</summary>
    public void StopGaugeMorph()
    {
        if (_player.GaugeDriver == null) return;
        _player.GaugeDriver  = null;
        _player.SmoothedFill = 0f;
        ResetGrowth();   // GaugeDriver now null → runs the normal reverse sweep / restore path
    }

    // ── Brio morph controls ───────────────────────────────────────────────────

    /// <summary>
    /// Starts a morph on the selected GPose actor. The origin is resolved in priority order:
    /// loaded MCDF → permanent C+ profile → identity (unscaled). Records the morph in Brio history.
    /// </summary>
    public void StartBrioMorph(float? speedOverride = null, MorphMode? modeOverride = null, EasingMode? easingOverride = null,
                               MorphTargetMode? targetModeOverride = null, Guid? easingCurveIdOverride = null)
    {
        if (SelectedBrioActorIndex < 0)
        {
            _log.Warning("[BodyFlux] No GPose actor selected.");
            return;
        }

        var resolvedTargetMode = targetModeOverride ?? _config.MorphTargetMode;

        Guid  destId; string destName; Guid? templateOwnerProfileId = null;
        if (resolvedTargetMode == MorphTargetMode.TemplateOverlay)
        {
            var templates = _ipc.Templates;
            if (SelectedBrioTemplateIndex < 0 || SelectedBrioTemplateIndex >= templates.Count)
            {
                _log.Warning("[BodyFlux] No overlay template selected.");
                return;
            }
            var t = templates[SelectedBrioTemplateIndex];
            destId = t.Id; destName = t.Name; templateOwnerProfileId = t.OwnerProfileId;
        }
        else
        {
            var profiles = _ipc.Profiles;
            if (SelectedBrioDestIndex < 0 || SelectedBrioDestIndex >= profiles.Count)
            {
                _log.Warning("[BodyFlux] No destination profile selected.");
                return;
            }
            (destId, destName) = profiles[SelectedBrioDestIndex];
        }

        var resolvedMode    = modeOverride   ?? _config.MorphMode;
        var resolvedEasing  = easingOverride ?? _config.BrioEasingMode;
        var resolvedCurveId = easingCurveIdOverride ?? _config.BrioEasingCurveId;

        if (!StartBrioMorphFor((ushort)SelectedBrioActorIndex, destId, _brioOriginJson,
                               speedOverride, resolvedMode, resolvedEasing, resolvedTargetMode, templateOwnerProfileId,
                               resolvedCurveId))
            return;

        // Record in Brio history (dedup by destination, newest first, cap at 5)
        var historyEntry = new MorphPreset(destId, destName,
            speedOverride ?? _config.BrioGrowthSpeed, resolvedMode, resolvedEasing,
            TargetMode: resolvedTargetMode, TemplateOwnerProfileId: templateOwnerProfileId,
            EasingCurveId: resolvedCurveId);
        _config.BrioRecentMorphs.RemoveAll(h => h.ProfileId == destId);
        _config.BrioRecentMorphs.Insert(0, historyEntry);
        if (_config.BrioRecentMorphs.Count > 5)
            _config.BrioRecentMorphs.RemoveAt(5);
        _config.Save();
    }

    /// <summary>
    /// Starts (or replaces) a morph on one Brio actor with fully explicit config — the engine
    /// behind both the single Apply and the Group tab's "Apply All". Origin resolves
    /// MCDF → permanent C+ profile → identity. <paramref name="speedOverride"/> null means
    /// "follow the live BrioGrowthSpeed slider". Returns false if the destination couldn't be read.
    /// </summary>
    public bool StartBrioMorphFor(ushort actorIndex, Guid destId, string? mcdfOriginJson,
                                  float? speedOverride, MorphMode mode, EasingMode easing,
                                  MorphTargetMode targetMode = MorphTargetMode.FullProfile,
                                  Guid? templateOwnerProfileId = null, Guid? easingCurveId = null)
    {
        var originJson = ResolveBrioOrigin(actorIndex, mcdfOriginJson, out string originSource);

        var (ec, destJson) = targetMode == MorphTargetMode.TemplateOverlay
            ? _ipc.GetTemplate(templateOwnerProfileId ?? Guid.Empty, destId)
            : _ipc.GetProfile(destId);
        if (ec != 0 || destJson == null)
        {
            _log.Error($"[BodyFlux/Brio] Fetching morph destination failed (ec={ec}).");
            return false;
        }

        var originJObj  = JObject.Parse(originJson);
        var destJObj    = JObject.Parse(destJson);
        var originBones = originJObj["Bones"] as JObject ?? new JObject();
        var destBones   = destJObj  ["Bones"] as JObject ?? new JObject();

        var effectiveDestBones = targetMode == MorphTargetMode.TemplateOverlay
            ? BoneJsonHelper.BuildOverlayDestination(originBones, destBones)
            : destBones;

        var session = new MorphSession
        {
            TargetIndex       = actorIndex,
            OriginProfileJson = originJson,
            NetworkTargetName = null,
            SpeedOverride     = speedOverride,
        };
        var resolvedCurve = easingCurveId is { } cid ? _config.CustomEasingCurves.Find(c => c.Id == cid) : null;
        TrySetupRootExternalisation(session, actorIndex);
        session.Controller.Start(actorIndex, originJObj, originBones, effectiveDestBones,
                                 mode, easing, session.RootExternalised, resolvedCurve);
        _brio[actorIndex] = session;

        _log.Information($"[BodyFlux/Brio] actor={actorIndex} bones={session.Controller.BoneCount} origin={originSource}");
        return true;
    }

    public void PauseBrioActor(ushort index)   { if (_brio.TryGetValue(index, out var s)) s.Controller.Pause(); }
    public void ResumeBrioActor(ushort index)  { if (_brio.TryGetValue(index, out var s)) s.Controller.Resume(); }
    public void ReverseBrioActor(ushort index) { if (_brio.TryGetValue(index, out var s)) s.Controller.Reverse(); }

    public void PauseAllBrio()   { foreach (var s in _brio.Values) s.Controller.Pause(); }
    public void ResumeAllBrio()  { foreach (var s in _brio.Values) s.Controller.Resume(); }
    public void ReverseAllBrio() { foreach (var s in _brio.Values) s.Controller.Reverse(); }

    public void ResetBrioActor(ushort index)
    {
        if (!_brio.TryGetValue(index, out var s)) return;
        ResetSession(s, isPlayer: false);
        _brio.Remove(index);
    }

    public void ResetAllBrio()
    {
        if (_brio.Count == 0) return;
        var indices = new ushort[_brio.Count];
        _brio.Keys.CopyTo(indices, 0);
        foreach (var idx in indices)
            ResetBrioActor(idx);
    }

    // ── Sequence playback ─────────────────────────────────────────────────────

    /// <summary>
    /// Plays a named <see cref="MorphSequence"/> on the local player. All step destination
    /// profiles are resolved up front (so a missing profile aborts before anything starts),
    /// the first step morphs from the currently-active profile, and each subsequent step
    /// morphs from the previous step's destination. Returns false if it could not start.
    /// </summary>
    public bool StartSequence(MorphSequence sequence, float? speedOverride = null)
    {
        if (_player.Controller.IsMorphing) return false;
        if (_player.GaugeDriver != null) return false; // a gauge morph owns the session — unbind it first
        if (sequence.Steps.Count == 0)
        {
            _log.Warning("[BodyFlux/Seq] Sequence has no steps.");
            return false;
        }

        // A pinned Origin Profile always wins over the live-active-profile read.
        string? originJson = null;
        if (_config.OriginProfileId is { } pinnedOriginId)
        {
            var (ecPin, pinnedJson) = _ipc.GetProfile(pinnedOriginId);
            if (ecPin == 0 && pinnedJson != null)
                originJson = pinnedJson;
            else
                _log.Warning($"[BodyFlux/Seq] Origin Profile could not be loaded (ec={ecPin}); " +
                             "falling back to the live active profile.");
        }

        if (originJson == null)
        {
            var (ec1, activeId) = _ipc.GetActiveProfileId(0);
            if (ec1 != 0 || !activeId.HasValue)
            {
                _log.Error($"[BodyFlux/Seq] GetActiveProfileId failed (ec={ec1}). " +
                           "Make sure a Customize+ profile is active.");
                return false;
            }
            var (ec2, json) = _ipc.GetProfile(activeId.Value);
            if (ec2 != 0 || json == null)
            {
                _log.Error($"[BodyFlux/Seq] GetProfile (origin) failed (ec={ec2}).");
                return false;
            }
            originJson = json;
        }

        var steps = new List<SeqStep>(sequence.Steps.Count);
        foreach (var step in sequence.Steps)
        {
            var (ec, destJson) = step.TargetMode == MorphTargetMode.TemplateOverlay
                ? _ipc.GetTemplate(step.TemplateOwnerProfileId ?? Guid.Empty, step.ProfileId)
                : _ipc.GetProfile(step.ProfileId);
            if (ec != 0 || destJson == null)
            {
                _log.Error($"[BodyFlux/Seq] '{step.ProfileName}' not found (ec={ec}); aborting sequence.");
                return false;
            }
            steps.Add(new SeqStep(destJson, speedOverride ?? step.Speed, step.Easing, step.TargetMode, step.EasingCurveId));
        }

        _player.SeqSteps          = steps;
        _player.SeqIndex          = 0;
        _player.SeqRunning        = true;
        _player.TargetIndex       = 0;
        _player.OriginProfileJson = originJson;
        _player.NetworkTargetName = null;

        TrySetupRootExternalisation(_player, 0);
        StartSequenceStep(_player, originJson, steps[0]);
        _log.Information($"[BodyFlux/Seq] Playing '{sequence.Name}' ({steps.Count} steps).");
        return true;
    }

    /// <summary>
    /// Plays a named <see cref="MorphSequence"/> on the currently-selected GPose actor. The
    /// first step's origin is resolved like <see cref="StartBrioMorph"/> (MCDF → permanent →
    /// identity); subsequent steps morph from the previous step's destination. Returns false
    /// if it could not start.
    /// </summary>
    public bool StartBrioSequence(MorphSequence sequence)
    {
        if (sequence.Steps.Count == 0)
        {
            _log.Warning("[BodyFlux/Seq] Brio sequence has no steps.");
            return false;
        }
        if (SelectedBrioActorIndex < 0)
        {
            _log.Warning("[BodyFlux/Seq] No GPose actor selected.");
            return false;
        }

        var actorIndex = (ushort)SelectedBrioActorIndex;

        string? originJson = null;
        string  originSource;
        if (_brioOriginJson != null)
        {
            originJson   = _brioOriginJson;
            originSource = "MCDF";
        }
        else
        {
            var (ec1, activeId) = _ipc.GetActiveProfileId(actorIndex);
            if (ec1 == 0 && activeId.HasValue)
            {
                var (ec2, j) = _ipc.GetProfile(activeId.Value);
                if (ec2 == 0 && j != null) originJson = j;
            }
            originSource = "permanent";
        }

        if (originJson == null)
        {
            _log.Warning($"[BodyFlux/Seq] Actor {actorIndex} has no readable origin; " +
                         "morphing from identity. Load an MCDF for correct scaling.");
            originJson   = """{"ID":"00000000-0000-0000-0000-000000000000","Name":"BodyFlux Origin","Bones":{}}""";
            originSource = "identity";
        }

        var steps = new List<SeqStep>(sequence.Steps.Count);
        foreach (var step in sequence.Steps)
        {
            // Overlay steps carry a Template id in ProfileId (not a profile id), so they must be
            // fetched via GetTemplate — same as the player sequence path in StartSequence. Calling
            // GetProfile on a template id fails and aborts the whole sequence, which is why overlay
            // sequences never played on Brio actors while full-profile ones did.
            var (ec, destJson) = step.TargetMode == MorphTargetMode.TemplateOverlay
                ? _ipc.GetTemplate(step.TemplateOwnerProfileId ?? Guid.Empty, step.ProfileId)
                : _ipc.GetProfile(step.ProfileId);
            if (ec != 0 || destJson == null)
            {
                _log.Error($"[BodyFlux/Seq] Profile '{step.ProfileName}' not found (ec={ec}); aborting sequence.");
                return false;
            }
            steps.Add(new SeqStep(destJson, step.Speed, step.Easing, step.TargetMode, step.EasingCurveId));
        }

        var session = new MorphSession
        {
            TargetIndex       = actorIndex,
            SeqSteps          = steps,
            SeqIndex          = 0,
            SeqRunning        = true,
            OriginProfileJson = originJson,
            NetworkTargetName = null,
        };
        TrySetupRootExternalisation(session, actorIndex);
        _brio[actorIndex] = session;

        StartSequenceStep(session, originJson, steps[0]);

        _log.Information($"[BodyFlux/Seq] Playing Brio '{sequence.Name}' on actor {actorIndex} " +
                         $"({steps.Count} steps, origin={originSource}).");
        return true;
    }

    private void StartSequenceStep(MorphSession s, string originJson, SeqStep step)
    {
        var originJObj  = JObject.Parse(originJson);
        var destJObj    = JObject.Parse(step.DestJson);
        var originBones = originJObj["Bones"] as JObject ?? new JObject();
        var destBones   = destJObj  ["Bones"] as JObject ?? new JObject();

        var effectiveDestBones = step.TargetMode == MorphTargetMode.TemplateOverlay
            ? BoneJsonHelper.BuildOverlayDestination(originBones, destBones)
            : destBones;

        // Remember the full-body profile this step actually lands on, so the next step chains its
        // origin from the real post-step appearance rather than the raw DestJson. For a Full Profile
        // step that is just the destination profile; for an overlay step it is origin ⊕ overlay (a
        // complete body), NOT the bare template — chaining from the template would blank every
        // non-overlaid bone to identity after the first step.
        var effectiveProfile = step.TargetMode == MorphTargetMode.TemplateOverlay
            ? new JObject(originJObj) { ["Bones"] = effectiveDestBones }
            : destJObj;
        s.SeqLastEffectiveJson = effectiveProfile.ToString(Newtonsoft.Json.Formatting.None);

        var stepCurve = step.EasingCurveId is { } scid ? _config.CustomEasingCurves.Find(c => c.Id == scid) : null;
        s.SpeedOverride = step.Speed;
        s.Controller.Start(s.TargetIndex, originJObj, originBones, effectiveDestBones,
                           MorphMode.Simple, step.Easing, s.RootExternalised, stepCurve);
        if (s == _player)
            _sync.ResetSendThrottle();
    }

    private void AdvanceSequence(MorphSession s)
    {
        int prev = s.SeqIndex;
        s.SeqIndex++;

        if (s.SeqSteps == null || s.SeqIndex >= s.SeqSteps.Count)
        {
            s.SeqRunning    = false;
            s.SeqSteps      = null;
            s.SeqIndex      = 0;
            s.SpeedOverride = null;
            _log.Information("[BodyFlux/Seq] Sequence complete.");
            return;
        }

        // Chain from the previous step's realized full-body result (origin ⊕ overlay for an overlay
        // step), not its raw DestJson — see MorphSession.SeqLastEffectiveJson.
        var nextOrigin = s.SeqLastEffectiveJson ?? s.SeqSteps[prev].DestJson;
        StartSequenceStep(s, nextOrigin, s.SeqSteps[s.SeqIndex]);
    }

    // ── Preset controls ───────────────────────────────────────────────────────

    /// <summary>
    /// Applies a saved preset or history entry, overriding current UI selections. Returns false
    /// (without starting a morph) if the preset's saved profile/template can no longer be resolved
    /// against Customize+ — e.g. it was renamed/deleted there, or the profile cache hasn't been
    /// populated yet.
    /// </summary>
    public bool ApplyMorphPreset(MorphPreset preset, float? speedOverride = null)
    {
        if (preset.TargetMode == MorphTargetMode.TemplateOverlay)
        {
            var templates = _ipc.Templates;
            int idx = -1;
            for (int i = 0; i < templates.Count; i++)
                if (templates[i].Id == preset.ProfileId) { idx = i; break; }
            if (idx < 0)
            {
                _log.Warning($"[BodyFlux] Preset template '{preset.ProfileName}' not found in Customize+.");
                return false;
            }
            SelectedTemplateIndex = idx;
        }
        else
        {
            var profiles = _ipc.Profiles;
            int idx = -1;
            for (int i = 0; i < profiles.Count; i++)
                if (profiles[i].Id == preset.ProfileId) { idx = i; break; }
            if (idx < 0)
            {
                _log.Warning($"[BodyFlux] Preset profile '{preset.ProfileName}' not found in Customize+.");
                return false;
            }
            SelectedProfileIndex = idx;
        }
        StartGrowth(speedOverride ?? preset.Speed, preset.Mode, preset.Easing, preset.TargetMode, preset.OriginProfileId,
                    preset.EasingCurveId);
        return true;
    }

    /// <summary>
    /// Applies a Brio preset or history entry. Besides the destination profile, a preset may
    /// also carry its own target actor and MCDF origin; when present, those are restored so the
    /// morph affects the originally-saved actor with the scaling it was saved against.
    /// </summary>
    public void ApplyBrioMorphPreset(MorphPreset preset, float? speedOverride = null)
    {
        if (preset.TargetMode == MorphTargetMode.TemplateOverlay)
        {
            var templates = _ipc.Templates;
            int idx = -1;
            for (int i = 0; i < templates.Count; i++)
                if (templates[i].Id == preset.ProfileId) { idx = i; break; }
            if (idx < 0)
            {
                _log.Warning($"[BodyFlux/Brio] Preset template '{preset.ProfileName}' not found in Customize+.");
                return;
            }
            SelectedBrioTemplateIndex = idx;
        }
        else
        {
            var profiles = _ipc.Profiles;
            int idx = -1;
            for (int i = 0; i < profiles.Count; i++)
                if (profiles[i].Id == preset.ProfileId) { idx = i; break; }
            if (idx < 0)
            {
                _log.Warning($"[BodyFlux/Brio] Preset profile '{preset.ProfileName}' not found in Customize+.");
                return;
            }
            SelectedBrioDestIndex = idx;
        }

        if (preset.TargetActorName != null)
        {
            int resolved = -1;
            foreach (var (aIdx, aName) in GetGPoseActors())
            {
                if (aName != preset.TargetActorName) continue;
                if (aIdx == preset.TargetActorIndex) { resolved = aIdx; break; }
                if (resolved < 0) resolved = aIdx;
            }
            if (resolved >= 0)
                SelectedBrioActorIndex = resolved;
            else
                _log.Warning($"[BodyFlux/Brio] Preset target actor '{preset.TargetActorName}' " +
                             "is not in the current scene; using the selected actor instead.");
        }

        if (preset.OriginMcdfJson != null)
        {
            _brioOriginJson = preset.OriginMcdfJson;
            BrioOriginLabel = preset.OriginMcdfLabel ?? "";
        }

        StartBrioMorph(speedOverride ?? preset.Speed, preset.Mode, preset.Easing, preset.TargetMode, preset.EasingCurveId);
    }

    /// <summary>Saves the current UI selection into preset slot <paramref name="slot"/> (0-based).</summary>
    public void SavePreset(int slot)
    {
        if (slot < 0 || slot >= Configuration.PresetSlots) return;

        Guid id; string name; Guid? templateOwnerProfileId = null;
        if (_config.MorphTargetMode == MorphTargetMode.TemplateOverlay)
        {
            var templates = _ipc.Templates;
            if (SelectedTemplateIndex < 0 || SelectedTemplateIndex >= templates.Count) return;
            var t = templates[SelectedTemplateIndex];
            id = t.Id; name = t.Name; templateOwnerProfileId = t.OwnerProfileId;
        }
        else
        {
            var profiles = _ipc.Profiles;
            if (SelectedProfileIndex < 0 || SelectedProfileIndex >= profiles.Count) return;
            (id, name) = profiles[SelectedProfileIndex];
        }

        while (_config.Presets.Count <= slot)
            _config.Presets.Add(null);
        // Captures the Origin Profile pin as it stands right now, so Apply later reproduces this
        // exact origin→destination pair instead of picking up whatever the pin has drifted to since
        // (the pin is one shared, global value — see MorphPreset.OriginProfileId).
        _config.Presets[slot] = new MorphPreset(id, name,
            _config.GrowthSpeed, _config.MorphMode, _config.EasingMode,
            TargetMode: _config.MorphTargetMode, TemplateOwnerProfileId: templateOwnerProfileId,
            OriginProfileId: _config.OriginProfileId,
            OriginProfileName: _config.OriginProfileId is { } originId ? ResolveProfileName(originId) : null,
            EasingCurveId: _config.EasingCurveId);
        _config.Save();
    }

    /// <summary>Clears preset slot <paramref name="slot"/> (0-based).</summary>
    public void ClearPreset(int slot)
    {
        if (slot >= 0 && slot < _config.Presets.Count)
        {
            _config.Presets[slot] = null;
            _config.Save();
        }
    }

    /// <summary>
    /// Saves the current Brio selection into preset slot <paramref name="slot"/> (0-based).
    /// Captures the destination profile plus the currently-selected actor and loaded MCDF
    /// origin, so the preset can re-target that actor with the same starting scaling.
    /// </summary>
    public void SaveBrioPreset(int slot)
    {
        if (slot < 0 || slot >= Configuration.PresetSlots) return;

        Guid id; string name; Guid? templateOwnerProfileId = null;
        if (_config.MorphTargetMode == MorphTargetMode.TemplateOverlay)
        {
            var templates = _ipc.Templates;
            if (SelectedBrioTemplateIndex < 0 || SelectedBrioTemplateIndex >= templates.Count) return;
            var t = templates[SelectedBrioTemplateIndex];
            id = t.Id; name = t.Name; templateOwnerProfileId = t.OwnerProfileId;
        }
        else
        {
            var profiles = _ipc.Profiles;
            if (SelectedBrioDestIndex < 0 || SelectedBrioDestIndex >= profiles.Count) return;
            (id, name) = profiles[SelectedBrioDestIndex];
        }

        string? actorName = null;
        int     actorIdx  = -1;
        if (SelectedBrioActorIndex >= 0)
        {
            var found = GetGPoseActors().Find(a => a.Index == (ushort)SelectedBrioActorIndex);
            if (found.Name != null) { actorName = found.Name; actorIdx = found.Index; }
        }

        while (_config.BrioPresets.Count <= slot)
            _config.BrioPresets.Add(null);
        _config.BrioPresets[slot] = new MorphPreset(id, name,
            _config.BrioGrowthSpeed, _config.MorphMode, _config.BrioEasingMode,
            _brioOriginJson, _brioOriginJson != null ? BrioOriginLabel : null,
            actorName, actorIdx, _config.MorphTargetMode, templateOwnerProfileId,
            EasingCurveId: _config.BrioEasingCurveId);
        _config.Save();
    }

    /// <summary>Clears Brio preset slot <paramref name="slot"/> (0-based).</summary>
    public void ClearBrioPreset(int slot)
    {
        if (slot >= 0 && slot < _config.BrioPresets.Count)
        {
            _config.BrioPresets[slot] = null;
            _config.Save();
        }
    }

    // ── MCDF helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Loads a Mare/Lightless MCDF file and extracts its Customize+ profile to use as the
    /// Brio clone's morph origin. Returns true on success; on failure <see cref="BrioOriginLabel"/>
    /// is set to the error reason.
    /// </summary>
    public bool LoadBrioOriginMcdf(string path)
    {
        var result = McdfReader.TryReadCustomizePlusProfile(path);
        if (!result.Ok)
        {
            _brioOriginJson = null;
            BrioOriginLabel = $"Error: {result.Error}";
            _log.Error($"[BodyFlux/Brio] Failed to read MCDF '{path}': {result.Error}");
            return false;
        }
        _brioOriginJson = result.CustomizePlusJson;
        BrioOriginLabel = System.IO.Path.GetFileName(path);
        _log.Information($"[BodyFlux/Brio] Loaded MCDF origin '{BrioOriginLabel}'.");
        return true;
    }

    /// <summary>Clears the currently loaded MCDF origin.</summary>
    public void ClearBrioOrigin()
    {
        _brioOriginJson = null;
        BrioOriginLabel = "";
    }

    /// <summary>
    /// Reads an MCDF's Customize+ profile without touching the shared MCDF staging — used by
    /// the Group tab, where each actor row keeps its own origin. Returns (ok, json, label-or-error).
    /// </summary>
    public (bool Ok, string? Json, string Label) ReadMcdfProfile(string path)
    {
        var result = McdfReader.TryReadCustomizePlusProfile(path);
        if (!result.Ok)
        {
            _log.Error($"[BodyFlux/Brio] Failed to read MCDF '{path}': {result.Error}");
            return (false, null, $"Error: {result.Error}");
        }
        return (true, result.CustomizePlusJson, System.IO.Path.GetFileName(path));
    }

    // ── GPose root externalisation ───────────────────────────────────────────

    /// <summary>
    /// Decides whether the morph's root bone should be driven through Brio's model transform
    /// instead of Customize+ (only in GPose, where Brio's pose-hold over n_root would otherwise
    /// fight C+ and cause a height flicker). When applicable, resolves the Brio actor that carries
    /// the rendered skeleton (for self-morphs that is the player's GPose counterpart, matched by
    /// name) and captures its current model scale so it can be restored on reset. Sets
    /// <see cref="MorphSession.RootExternalised"/>, <see cref="MorphSession.ModelTransformIndex"/>,
    /// and <see cref="MorphSession.OriginalModelScale"/> on <paramref name="s"/>.
    /// </summary>
    private void TrySetupRootExternalisation(MorphSession s, ushort cPlusTargetIndex)
    {
        s.RootExternalised   = false;
        s.OriginalModelScale = null;
        s.ModelTransformIndex = null;

        if (!_clientState.IsGPosing || !_brioIpc.IsAvailable) return;

        // Resolve which Brio-managed actor carries the rendered skeleton.
        ushort? brioIndex = null;
        var managed = _brioIpc.GetManagedActors();
        if (cPlusTargetIndex == 0)
        {
            // Self-morph: C+ targets index 0, but Brio poses the player's GPose counterpart.
            var name = _objects[0]?.Name.TextValue;
            if (!string.IsNullOrEmpty(name))
                foreach (var (idx, n) in managed)
                    if (n == name) { brioIndex = idx; break; }
        }
        else
        {
            foreach (var (idx, _) in managed)
                if (idx == cPlusTargetIndex) { brioIndex = idx; break; }
        }
        if (brioIndex == null) return;

        var actor = _objects[brioIndex.Value];
        if (actor == null) return;

        // Reuse the remembered pre-morph scale if a previous (un-reset) morph is still driving this
        // actor; only read the live value when we are the first to externalise it. This prevents a
        // chained morph from capturing the previous morph's inflated scale as the "original".
        if (_externalBaseScale.TryGetValue(brioIndex.Value, out var known))
        {
            s.OriginalModelScale = known;
        }
        else
        {
            var (_, _, scale) = _brioIpc.GetModelTransform(actor);
            s.OriginalModelScale = scale ?? Vector3.One;
            _externalBaseScale[brioIndex.Value] = s.OriginalModelScale.Value;
        }

        s.ModelTransformIndex = brioIndex.Value;
        s.RootExternalised    = true;
        _log.Information($"[BodyFlux/Brio] Root externalised via Brio model transform " +
                         $"(C+ target={cPlusTargetIndex}, Brio actor={brioIndex.Value}, base={s.OriginalModelScale}).");
    }

    /// <summary>Restores the actor's Brio model scale captured at morph start (if externalised).</summary>
    private void RestoreExternalRootScale(MorphSession s)
    {
        if (!s.RootExternalised || s.ModelTransformIndex is not { } idx) return;

        var actor = _objects[idx];
        if (actor != null)
            _brioIpc.SetModelTransform(actor, position: null, rotation: null,
                                       scale: s.OriginalModelScale ?? Vector3.One);

        // The true original has now been put back — forget it so the next morph re-reads fresh.
        _externalBaseScale.Remove(idx);
        _log.Information($"[BodyFlux/Brio] Root scale restored on actor {idx} " +
                         $"(scale={s.OriginalModelScale ?? Vector3.One}).");
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>Looks up a Customize+ profile's display name by ID, for snapshotting into a preset's
    /// <see cref="MorphPreset.OriginProfileName"/>. Returns <c>null</c> if not found.</summary>
    private string? ResolveProfileName(Guid id)
    {
        var profiles = _ipc.Profiles;
        for (int i = 0; i < profiles.Count; i++)
            if (profiles[i].Id == id) return profiles[i].Name;
        return null;
    }

    /// <summary>
    /// Resolves the local player's morph origin: <paramref name="originOverride"/> wins if given
    /// (a preset's own saved Origin Profile — see <see cref="MorphPreset.OriginProfileId"/>), else
    /// the globally pinned Origin Profile, then the live-active C+ profile, then a temp profile
    /// already applied by another plugin. Shared by <see cref="StartGrowth"/> and the instant-apply
    /// path in <see cref="ResetGrowth"/> so both agree on what "Origin" currently means.
    /// </summary>
    private string? ResolvePlayerOriginJson(Guid? originOverride = null)
    {
        if (originOverride is { } presetOriginId)
        {
            var (ecPreset, presetJson) = _ipc.GetProfile(presetOriginId);
            if (ecPreset == 0 && presetJson != null)
                return presetJson;
            _log.Warning($"[BodyFlux] Preset's saved Origin Profile could not be loaded (ec={ecPreset}); " +
                         "falling back to the pinned/live origin.");
        }

        if (_config.OriginProfileId is { } pinnedOriginId)
        {
            var (ecPin, pinnedJson) = _ipc.GetProfile(pinnedOriginId);
            if (ecPin == 0 && pinnedJson != null)
                return pinnedJson;
            _log.Warning($"[BodyFlux] Origin Profile could not be loaded (ec={ecPin}); " +
                         "falling back to the live active profile.");
        }

        var (ec1, activeId) = _ipc.GetActiveProfileId(0);
        if (ec1 == 0 && activeId.HasValue)
        {
            var (ec2, json) = _ipc.GetProfile(activeId.Value);
            if (ec2 == 0 && json != null) return json;
        }

        // Fallback: a temp profile already applied (e.g. another plugin).
        var (ecT, tempJson) = _ipc.GetTempProfile(0);
        return ecT == 0 && tempJson != null ? tempJson : null;
    }

    private string ResolveBrioOrigin(ushort actorIndex, string? mcdfJson, out string source)
    {
        if (mcdfJson != null)
        {
            source = "MCDF";
            return mcdfJson;
        }

        var (ec1, activeId) = _ipc.GetActiveProfileId(actorIndex);
        if (ec1 == 0 && activeId.HasValue)
        {
            var (ec2, j) = _ipc.GetProfile(activeId.Value);
            if (ec2 == 0 && j != null) { source = "permanent"; return j; }
        }

        _log.Warning($"[BodyFlux/Brio] Actor {actorIndex} has no readable origin; " +
                     "morphing from identity (unscaled). Load an MCDF for correct scaling.");
        source = "identity";
        return """{"ID":"00000000-0000-0000-0000-000000000000","Name":"BodyFlux Origin","Bones":{}}""";
    }

    private void ResetSession(MorphSession s, bool isPlayer)
    {
        var targetIndex = s.Controller.TargetIndex;

        // Reset Active only ever applies to the local player (targetIndex 0) — for a Brio actor or
        // a remote peer, OriginProfileJson is the only known-good state to hand back (deleting the
        // temp profile there would strip any other plugin's overlay, e.g. Mare/Lightless, on a
        // target we don't own outside our own morph).
        bool restoreOrigin = targetIndex != 0 || !s.ResetToLiveActive;

        if (isPlayer)
        {
            if (s.NetworkTargetName != null)
                _sync.SendStop(s.NetworkTargetName);
            else if (restoreOrigin && s.OriginProfileJson != null)
                _sync.SendFrame(s.OriginProfileJson, null);
            else
                _sync.SendStop(null);
        }

        // Restore the Brio model scale we drove during the morph (GPose root externalisation)
        // before tearing down the session.
        RestoreExternalRootScale(s);

        s.Controller.Stop();
        s.SpeedOverride = null;
        s.SeqRunning    = false;
        s.SeqSteps      = null;
        s.SeqIndex      = 0;
        s.Resetting           = false;
        s.RootExternalised    = false;
        s.ModelTransformIndex = null;
        s.OriginalModelScale  = null;

        if (restoreOrigin && s.OriginProfileJson != null)
            _ipc.SetTempProfile(targetIndex, s.OriginProfileJson);
        else
            _ipc.DeleteTempProfile(targetIndex);

        s.OriginProfileJson    = null;
        s.NetworkTargetName    = null;
        s.CompletedRemoteJson  = null;
        s.ResetToLiveActive    = false;
    }

    private ushort? FindCharacterIndex(string characterName)
    {
        for (ushort i = 1; i < _objects.Length; i++)
        {
            var obj = _objects[i];
            if (obj != null && obj.Name.TextValue == characterName)
                return i;
        }
        return null;
    }
}
