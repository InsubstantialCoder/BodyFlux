using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using BodyFlux.Morph;
using BodyFlux.Services;

namespace BodyFlux.Windows.Tabs;

/// <summary>
/// Renders the "Sync" tab: the group Sync Key, the remote-morph consent toggle, connection
/// controls, status, and a grid of connected peers each with their own morph config and Apply,
/// so several peers can be morphed at once (plus a general Apply All / Pause All / etc. row).
/// </summary>
public sealed class SyncTabView
{
    private readonly Plugin plugin;

    // One configurable entry per connected peer, created lazily the first time that peer's name is
    // seen (unlike Brio's Multi tab, peers aren't manually added — they come from ConsentingPeers).
    private sealed class TargetConfig
    {
        public int             DestIndex     = -1; // index into SavedProfiles
        public int             TemplateIndex = -1; // index into SavedTemplates (TargetMode == TemplateOverlay)
        public MorphTargetMode TargetMode = MorphTargetMode.FullProfile;
        public float           Speed;
        public MorphMode       Mode   = MorphMode.Simple;
        public EasingMode      Easing = EasingMode.Linear;
        public Guid?           EasingCurveId;
    }
    private readonly Dictionary<string, TargetConfig> _targetConfigs = new();
    private string _targetProfileFilter = ""; // shared filter for every card's destination picker

    // Short target labels so the ButtonGroup fits on the SectionLabel's line inside a compact card
    // (mirrors BrioTabView's GroupTargetLabels).
    private static readonly string[] TargetModeLabels = ["Full", "Overlay"];

    // Off/Ask/Auto, indexed by (int)ConsentMode.
    private static readonly string[] ConsentModeLabels = ["Off", "Ask", "Auto"];

    public SyncTabView(Plugin plugin) => this.plugin = plugin;

    private static string GeneratePairKey()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var buf = new char[9];
        for (int i = 0; i < 4; i++) buf[i] = chars[Random.Shared.Next(chars.Length)];
        buf[4] = '-';
        for (int i = 5; i < 9; i++) buf[i] = chars[Random.Shared.Next(chars.Length)];
        return new string(buf);
    }

    public void Draw()
    {
        using var scroll = ImRaii.Child("##SyncScroll", new Vector2(0, 0), false);
        if (!scroll) return;

        float bw     = 90 * ImGuiHelpers.GlobalScale;
        float labelW = 90 * ImGuiHelpers.GlobalScale;
        var   config = plugin.Configuration;

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            "Generate a Sync Key and share it with your partner(s) to sync together " +
            "and see each other's morphs in real time.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Enable toggle ─────────────────────────────────────────────────────
        bool syncEnabled = config.NetworkSyncEnabled;
        UiHelpers.SectionLabel("Enable Group Sync:",
            "Turns Sync on for this session. Once enabled, generate or enter a Sync Key below and " +
            "press Connect to start sharing morphs with your partner(s) in real time.");
        ImGui.SameLine();
        if (ImGui.Checkbox("##EnableGroupSync", ref syncEnabled))
        {
            config.NetworkSyncEnabled = syncEnabled;
            config.Save();
            if (!syncEnabled)
                plugin.DisconnectNetworkSync();
        }

        ImGui.Spacing();

        UiHelpers.SectionLabel("Allow Morph Requests:",
            "Off: no one can request to morph you, your Request button is disabled on every " +
            "other player's client. Ask: connected players can send a request, but you always get " +
            "an Accept/Deny prompt for each one (the default). Auto: requests from anyone with your " +
            "Sync Key are granted immediately, no prompt.");
        ImGui.SameLine();
        int consentModeInt = (int)plugin.MorphRequestMode;
        if (UiHelpers.ButtonGroup("ConsentMode", ConsentModeLabels, ref consentModeInt))
            plugin.MorphRequestMode = (ConsentMode)consentModeInt;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Sync Key row ──────────────────────────────────────────────────────
        // Fixed width instead of stretching to fill the row: the key is always "XXXX-XXXX" (9
        // chars), so a wide field just pushes Generate/Copy/Connect toward the right edge for no
        // reason — this keeps them right after the field instead.
        float keyFieldW = 120 * ImGuiHelpers.GlobalScale;

        bool isActive = plugin.IsNetworkActive;

        UiHelpers.SectionLabel("Sync Key:", null);
        ImGui.SameLine(labelW);
        if (isActive) ImGui.BeginDisabled();
        ImGui.SetNextItemWidth(keyFieldW);
        string pairKey = config.PairKey;
        if (ImGui.InputText("##SyncKey", ref pairKey, 64))
        {
            config.PairKey = pairKey;
            config.Save();
        }

        ImGui.SameLine();
        if (UiHelpers.PrimaryButton("Generate", new Vector2(bw, 0)))
        {
            config.PairKey = GeneratePairKey();
            config.Save();
        }
        if (isActive) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##copySyncKey", FontAwesomeIcon.Copy))
            ImGui.SetClipboardText(config.PairKey);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy Sync Key to clipboard");

        ImGui.SameLine();
        if (plugin.IsNetworkActive)
        {
            if (UiHelpers.DangerButton("Disconnect", new Vector2(bw, 0)))
                plugin.DisconnectNetworkSync();
        }
        else if (plugin.IsNetworkIdleDisconnected || plugin.IsNetworkQuotaExhausted)
        {
            if (UiHelpers.PrimaryButton("Reconnect", new Vector2(bw, 0)))
                plugin.ApplyNetworkSyncConfig();
        }
        else
        {
            if (UiHelpers.PrimaryButton("Connect", new Vector2(bw, 0)))
                plugin.ApplyNetworkSyncConfig();
        }

        // ── Connection status ─────────────────────────────────────────────────
        ImGui.Spacing();
        var status = plugin.NetworkSyncStatus;
        var statusColor = status == "Connected"
            ? new Vector4(0.3f, 0.9f, 0.4f, 1f)
            : status.StartsWith("Connecting")
                ? new Vector4(0.9f, 0.8f, 0.2f, 1f)
                : status is "Disconnected (idle)" or "Daily quota exceeded"
                    ? new Vector4(0.9f, 0.6f, 0.2f, 1f)  // amber — auto-disconnected
                    : new Vector4(0.7f, 0.7f, 0.7f, 1f);
        ImGui.TextColored(statusColor, $"Status: {status}");

        // ── Daily quota exhausted notice ──────────────────────────────────────
        if (plugin.IsNetworkQuotaExhausted)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(new Vector4(0.9f, 0.4f, 0.2f, 1f),
                "Daily relay quota exceeded. Sync is paused — please try again tomorrow.");
            ImGui.PopTextWrapPos();
        }

        // ── Lone-connection auto-disconnect notice ────────────────────────────
        // The relay only needs to carry frames between connected players, so a lone connection is
        // auto-disconnected to save bandwidth. Show the running countdown while alone.
        if (plugin.IsNetworkAwaitingIdleDisconnect)
        {
            int secs = plugin.NetworkIdleDisconnectSecondsRemaining;
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(new Vector4(0.9f, 0.6f, 0.2f, 1f),
                $"No peers connected to Sync Key... Auto-disconnecting in {secs / 60}:{secs % 60:D2}");
            ImGui.PopTextWrapPos();
        }

        // ── Connected users ───────────────────────────────────────────────────
        // One consolidated listing instead of a plain peer-name list plus a separate quick-select
        // grid, the grid already shows everyone targetable so a second, redundant list added
        // nothing.
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        var maxPeers = plugin.NetworkMaxPeers;
        if (maxPeers.HasValue)
            ImGui.TextDisabled($"Up to {maxPeers} players can share the same Sync Key simultaneously.");
        UiHelpers.SectionLabel("Connected Users:",
            "Every current member of your Sync Key. Configure a destination, speed, mode and " +
            "easing for any of them, then Request that card on its own or use Request All below to " +
            "ask several at once. A peer whose Allow Morph Requests is Off has their button " +
            "disabled since they can't be requested.");
        ImGui.Spacing();

#if DEBUG
        // Debug-only: lets a solo developer populate the grid below and exercise the whole
        // Request → Pending → Granted flow without a second real player. The fake peer self-grants
        // consent shortly after being requested (see SyncManager.RequestConsent).
        using (ImRaii.Disabled(!plugin.IsNetworkActive))
        {
            if (ImGui.Button("Add Dummy Peer (Debug)"))
                plugin.AddDebugSyncPeer();
        }
        if (!plugin.IsNetworkActive && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Connect to Sync first.");

        ImGui.SameLine();
        if (UiHelpers.DangerButton("Clear Dummy Peers (Debug)", Vector2.Zero))
            plugin.ClearDebugSyncPeers();

        ImGui.Spacing();
#endif

        DrawTargetGrid();
    }

    private void DrawTargetGrid()
    {
        var targets = plugin.AllSyncPeers;

        if (!plugin.IsNetworkActive)
        {
            ImGui.TextDisabled("Connect to Sync to see targetable peers here.");
            return;
        }
        if (targets.Count == 0)
        {
            ImGui.TextDisabled("No peers connected right now.");
            return;
        }

        // Extra cell padding so each card gets breathing room from its neighbor, from the tab's own
        // right edge, and from the row below it (the card's own internal pad alone, see
        // UiHelpers.Card, wasn't enough once it sat inside a table cell with barely any padding of
        // its own — vertical padding matters here too since peers wrap to a new grid row).
        using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding,
                   new Vector2(10 * ImGuiHelpers.GlobalScale, 8 * ImGuiHelpers.GlobalScale)))
        using (var table = ImRaii.Table("##SyncTargetGrid", 2))
        {
            if (table)
            {
                ImGui.TableSetupColumn("##a", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("##b", ImGuiTableColumnFlags.WidthStretch, 1f);

                foreach (var peer in targets)
                {
                    var cfg = GetOrCreateConfig(peer);
                    ImGui.TableNextColumn();
                    ImGui.PushID(peer);
                    UiHelpers.Card(innerWidth => DrawTargetCard(peer, cfg, innerWidth));
                    ImGui.PopID();
                }
            }
        }

        ImGui.Spacing();
        // Off peers can't be requested at all, Request All should only act on the rest.
        var requestable = targets.Where(p => plugin.ConsentingPeers.Contains(p)).ToList();
        DrawAllControlsRow(requestable);
    }

    private TargetConfig GetOrCreateConfig(string peerName)
    {
        if (_targetConfigs.TryGetValue(peerName, out var cfg)) return cfg;
        cfg = new TargetConfig { Speed = plugin.Configuration.GrowthSpeed };
        _targetConfigs[peerName] = cfg;
        return cfg;
    }

    private static bool HasValidDestination(Plugin plugin, TargetConfig cfg) => cfg.TargetMode == MorphTargetMode.TemplateOverlay
        ? cfg.TemplateIndex >= 0 && cfg.TemplateIndex < plugin.SavedTemplates.Count
        : cfg.DestIndex     >= 0 && cfg.DestIndex     < plugin.SavedProfiles.Count;

    /// <summary>Draws one peer's config editor (mirrors BrioTabView's Group row) plus its own
    /// consent-gated Request/Waiting/Apply button and a live progress bar while it's active.</summary>
    private void DrawTargetCard(string peerName, TargetConfig cfg, float innerWidth)
    {
        float scale = ImGuiHelpers.GlobalScale;
        // GetContentRegionAvail() inside a Card reports width against the raw cell edge, not the
        // card's own right inset (Indent only accounts for the left side of Card's padding), so any
        // "rest of the row" width for a same-line widget must be measured from innerWidth instead —
        // otherwise it draws flush against the card's border. leftX anchors that measurement.
        float leftX = ImGui.GetCursorPosX();
        float Remaining() => innerWidth - (ImGui.GetCursorPosX() - leftX);

        ImGui.TextUnformatted(peerName);
        ImGui.Spacing();

        // ── Target mode ────────────────────────────────────────────────────────
        UiHelpers.SectionLabel("Target:", null);
        ImGui.SameLine();
        int targetModeInt = (int)cfg.TargetMode;
        if (UiHelpers.ButtonGroup("tgt", TargetModeLabels, ref targetModeInt, Remaining()))
            cfg.TargetMode = (MorphTargetMode)targetModeInt;
        ImGui.Spacing();

        // ── Destination ───────────────────────────────────────────────────────
        bool overlay = cfg.TargetMode == MorphTargetMode.TemplateOverlay;
        UiHelpers.SectionLabel(overlay ? "Overlay:" : "Destination:", null);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Remaining());
        if (overlay)
        {
            var names = plugin.SavedTemplates.Select(t => t.Name).ToList();
            int  idx  = cfg.TemplateIndex;
            UiHelpers.DrawFilterableCombo("##dest", "##destfilter", names,
                ref idx, ref _targetProfileFilter, "— select a template —");
            cfg.TemplateIndex = idx;
        }
        else
        {
            var names = plugin.SavedProfiles.Select(p => p.Name).ToList();
            int  idx  = cfg.DestIndex;
            UiHelpers.DrawFilterableCombo("##dest", "##destfilter", names,
                ref idx, ref _targetProfileFilter, "— select a profile —");
            cfg.DestIndex = idx;
        }
        ImGui.Spacing();

        // ── Speed ─────────────────────────────────────────────────────────────
        UiHelpers.SectionLabel("Morph Time:",
            "How long the morph takes to complete, in seconds, lower is faster. " +
            "Ctrl+Click the slider to type an exact value.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Remaining());
        float spTime = cfg.Speed > 0f ? 1f / cfg.Speed : 100f;
        if (UiHelpers.GradientSlider("##speed", ref spTime, 0.10f, 100f, "%.2f s", ImGuiSliderFlags.Logarithmic))
            cfg.Speed = spTime > 0f ? 1f / spTime : 0.01f;

        ImGui.TextDisabled($"Time to complete: {spTime:F2}s   •   Speed: {cfg.Speed:F2} range/sec");
        ImGui.Spacing();

        // ── Mode ──────────────────────────────────────────────────────────────
        UiHelpers.SectionLabel("Morph Loop:", null);
        ImGui.SameLine();
        int modeInt = (int)cfg.Mode;
        if (UiHelpers.ButtonGroup("mode", UiHelpers.MorphModeLabels, ref modeInt, Remaining()))
            cfg.Mode = (MorphMode)modeInt;
        ImGui.Spacing();

        // ── Easing ────────────────────────────────────────────────────────────
        UiHelpers.SectionLabel("Easing:", null);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160 * scale);
        var easingMode = cfg.Easing;
        var curveId    = cfg.EasingCurveId;
        if (UiHelpers.EasingCombo("##easing", plugin.Configuration, ref easingMode, ref curveId))
        {
            cfg.Easing        = easingMode;
            cfg.EasingCurveId = curveId;
        }

        ImGui.Spacing();

        // ── Live progress (while this peer's morph is running) ─────────────────
        if (plugin.IsNetworkTargetActive(peerName))
        {
            float prog = plugin.GetNetworkTargetProgress(peerName);
            UiHelpers.GradientProgressBar(prog, new Vector2(innerWidth, 14 * scale), $"{prog * 100f:F0}%");
            ImGui.Spacing();
        }

        // ── Consent-gated Request/Waiting/Apply ─────────────────────────────────
        bool hasDestination = HasValidDestination(plugin, cfg);
        var  consent        = plugin.GetConsentForTarget(peerName);
        bool requestable    = plugin.ConsentingPeers.Contains(peerName);

        if (!requestable)
        {
            using (ImRaii.Disabled(true))
                ImGui.Button("Request", new Vector2(innerWidth, 0));
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip($"{peerName} has Allow Morph Requests set to Off, they can't be requested.");
        }
        else if (consent == ConsentStatus.Pending)
        {
            using (ImRaii.Disabled(true))
                ImGui.Button("Waiting…", new Vector2(innerWidth, 0));
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip($"Waiting for {peerName} to respond to your request…");
        }
        else
        {
            string label = consent == ConsentStatus.Granted ? "Apply" : "Request";
            using (ImRaii.Disabled(!hasDestination))
            {
                if (UiHelpers.PrimaryButton(label, new Vector2(innerWidth, 0)))
                    TriggerCardAction(peerName, cfg);
            }
            if (!hasDestination && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(overlay ? "Select an overlay template first." : "Select a destination profile first.");
        }
    }

    /// <summary>
    /// Resolves the card's configured destination and either sends a consent request (Idle) or
    /// starts the morph directly (Granted) — the action behind both a card's own button and
    /// Apply All. No-ops silently if the destination can no longer be resolved.
    /// </summary>
    private void TriggerCardAction(string peerName, TargetConfig cfg)
    {
        bool  overlay = cfg.TargetMode == MorphTargetMode.TemplateOverlay;
        Guid  destId;
        Guid? templateOwnerProfileId = null;
        if (overlay)
        {
            if (cfg.TemplateIndex < 0 || cfg.TemplateIndex >= plugin.SavedTemplates.Count) return;
            var t = plugin.SavedTemplates[cfg.TemplateIndex];
            destId = t.Id; templateOwnerProfileId = t.OwnerProfileId;
        }
        else
        {
            if (cfg.DestIndex < 0 || cfg.DestIndex >= plugin.SavedProfiles.Count) return;
            destId = plugin.SavedProfiles[cfg.DestIndex].Id;
        }

        var consent = plugin.GetConsentForTarget(peerName);
        if (consent == ConsentStatus.Granted)
            plugin.StartNetworkMorphFor(peerName, destId, cfg.Speed, cfg.Mode, cfg.Easing,
                cfg.TargetMode, templateOwnerProfileId, cfg.EasingCurveId);
        else if (consent == ConsentStatus.Idle)
            plugin.RequestNetworkMorphConsent(peerName, destId, cfg.Speed, cfg.Mode, cfg.Easing,
                cfg.TargetMode, templateOwnerProfileId, cfg.EasingCurveId);
    }

    /// <summary>Apply All (drives every configured card's own Request-or-Apply action) plus the
    /// Pause All/Resume All/Reverse All/Reset All row, mirroring BrioTabView's Group controls.</summary>
    private void DrawAllControlsRow(IReadOnlyList<string> targets)
    {
        float bw    = 90 * ImGuiHelpers.GlobalScale;
        float scale = ImGuiHelpers.GlobalScale;

        int applyable = targets.Count(peer =>
            _targetConfigs.TryGetValue(peer, out var cfg) && HasValidDestination(plugin, cfg));

        using (ImRaii.Disabled(applyable == 0))
        {
            if (UiHelpers.PrimaryButton($"Request All ({applyable})", new Vector2(bw + 40 * scale, 0)))
            {
                foreach (var peer in targets)
                {
                    if (_targetConfigs.TryGetValue(peer, out var cfg) && HasValidDestination(plugin, cfg))
                        TriggerCardAction(peer, cfg);
                }
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Pause All", new Vector2(bw, 0))) plugin.PauseAllNetworkTargets();
        ImGui.SameLine();
        if (UiHelpers.PrimaryButton("Resume All", new Vector2(bw, 0))) plugin.ResumeAllNetworkTargets();
        ImGui.SameLine();
        if (ImGui.Button("Reverse All", new Vector2(bw, 0))) plugin.ReverseAllNetworkTargets();
        ImGui.SameLine();
        if (UiHelpers.DangerButton("Reset All", new Vector2(bw, 0))) plugin.ResetAllNetworkTargets();
    }
}
