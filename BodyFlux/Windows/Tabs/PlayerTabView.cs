using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using BodyFlux.Morph;
using BodyFlux.Services;
using BodyFlux.Windows;

namespace BodyFlux.Windows.Tabs;

/// <summary>
/// Renders the "Player" tab and its sub-tabs (Single, Presets, Sequences): destination/speed/mode/
/// easing options, target selector, progress + controls, recent-morph history, and the preset grid.
/// </summary>
public sealed class PlayerTabView
{
    private readonly Plugin            plugin;
    private readonly SequenceListView  seqList;
    private readonly Action            onResetProfileCache; // ask MainWindow to refresh profiles next OnOpen

    private string          _profileFilter   = "";
    private string          _originProfileFilter = ""; // filter for the Default Origin Profile picker
    private int             _seqPlayingIndex = -1; // sequence active/resting (-1 = none)
    private string          _seqAddFilter    = ""; // filter for the "Add step" picker
    private MorphTargetMode _seqAddMode      = MorphTargetMode.FullProfile; // "Add step" target mode

    public PlayerTabView(Plugin plugin, SequenceListView seqList, Action onResetProfileCache)
    {
        this.plugin              = plugin;
        this.seqList             = seqList;
        this.onResetProfileCache = onResetProfileCache;
    }

    public void Draw()
    {
        using var subTabs = ImRaii.TabBar("##PlayerSubTabs");
        if (!subTabs) return;

        using (var tab = ImRaii.TabItem("Single"))
            if (tab) DrawMorphSubTab();

        using (var tab = ImRaii.TabItem("Presets"))
            if (tab) DrawPresetsTab();

        using (var tab = ImRaii.TabItem("Sequences"))
            if (tab) DrawSequencesSubTab();

        using (var tab = ImRaii.TabItem("Gauge Bind"))
            if (tab) DrawGaugeBindSubTab();
    }

    private void DrawMorphSubTab()
    {
        using var scroll = ImRaii.Child("##PlayerMorphScroll", new Vector2(0, 0), false);
        if (!scroll) return;

        ImGui.Spacing();

        // ── Target actor: its own full-width row above the columns, matching Gpose's
        //    actor-selector-on-top layout.
        DrawTargetSelector();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Spacing();

        // ── Two columns: Origin/Destination on the left, Speed/Mode/Easing on the right.
        //    Radio rows and the filterable combo stay the widgets they already were, only
        //    the surrounding layout changes. A vertical border plus extra cell padding keeps
        //    the two groups of controls from reading as one continuous block.
        using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding,
                   new Vector2(14 * ImGuiHelpers.GlobalScale, ImGui.GetStyle().CellPadding.Y)))
        using (var table = ImRaii.Table("##PlayerColumns", 2, ImGuiTableFlags.BordersInnerV))
        {
            if (table)
            {
                ImGui.TableSetupColumn("##left",  ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("##right", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                DrawLeftColumn();

                ImGui.TableSetColumnIndex(1);
                DrawRightColumn();
            }
        }

        // ── Footer: always visible ────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Spacing();
        UiHelpers.Card(DrawMorphingCard);

        DrawHistory();
    }

    private void DrawLeftColumn()
    {
        var config = plugin.Configuration;
        DrawOriginProfilePicker(config);

        ImGui.Spacing();
        ImGui.Spacing();
        DrawMorphTargetSelector(config);

        ImGui.Spacing();
        ImGui.Spacing();
        DrawDestinationPicker(config);
    }

    private void DrawRightColumn()
    {
        var config = plugin.Configuration;

        DrawGrowthSpeed(config);

        ImGui.Spacing();
        ImGui.Spacing();
        DrawMorphModeSelector(config);

        ImGui.Spacing();
        ImGui.Spacing();
        DrawEasingSelector(config);
    }

    /// <summary>
    /// Picker for pinning a saved Customize+ profile as the always-used origin for the local
    /// player's Single and Sequence morphs, overriding the live-active-profile read. Reverse and
    /// "Reset to Origin" both land on this pinned profile; "Reset to Active" ignores it and
    /// restores whatever's really active on the character.
    /// </summary>
    private void DrawOriginProfilePicker(Configuration config)
    {
        UiHelpers.SectionLabel("Origin Profile:",
            "Shows the Customize+ profile currently active on your character. When set to a saved " +
            "profile instead, every morph on yourself (Single and Sequences) always starts from that " +
            "one rather than whatever's currently active. Reverse and Reset to Origin both play back " +
            "toward it; Reset to Active ignores it and restores your character's real active profile.");
        ImGui.Spacing();

        var names = new List<string> { $"— use live active profile ({plugin.ActiveProfileName}) —" };
        names.AddRange(plugin.SavedProfiles.Select(p => p.Name));

        int idx = 0;
        if (config.OriginProfileId is { } pinnedId)
        {
            for (int i = 0; i < plugin.SavedProfiles.Count; i++)
                if (plugin.SavedProfiles[i].Id == pinnedId) { idx = i + 1; break; }
        }
        int prevIdx = idx;

        using (ImRaii.Disabled(plugin.IsMorphing))
        {
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            UiHelpers.DrawFilterableCombo("##OriginProfile", "##OriginProfileFilter", names,
                ref idx, ref _originProfileFilter, names[0]);
        }

        if (idx != prevIdx)
        {
            config.OriginProfileId = idx == 0 ? null : plugin.SavedProfiles[idx - 1].Id;
            config.Save();
        }
    }

    private void DrawDestinationPicker(Configuration config)
    {
        bool busy    = plugin.IsMorphing;
        bool overlay = config.MorphTargetMode == MorphTargetMode.TemplateOverlay;

        UiHelpers.SectionLabel(overlay ? "Overlay Template:" : "Destination Profile:",
            overlay
                ? "The Customize+ Template the morph plays toward. Only templates attached to at least " +
                  "one profile show up here."
                : "The saved Customize+ profile the morph plays toward, pick one, then Apply below.");
        ImGui.Spacing();

        float bw = 90 * ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);

        using (ImRaii.Disabled(busy))
        {
            if (overlay)
            {
                var names = plugin.SavedTemplates.Select(t => t.Name).ToList();
                int  idx  = plugin.SelectedTemplateIndex;
                UiHelpers.DrawFilterableCombo("##DestTemplate", "##TemplateFilter", names,
                    ref idx, ref _profileFilter, "— select a template —");
                plugin.SelectedTemplateIndex = idx;
            }
            else
            {
                var names = plugin.SavedProfiles.Select(p => p.Name).ToList();
                int  idx  = plugin.SelectedProfileIndex;
                UiHelpers.DrawFilterableCombo("##DestProfile", "##ProfileFilter", names,
                    ref idx, ref _profileFilter, "— select a profile —");
                plugin.SelectedProfileIndex = idx;
            }
        }

        ImGui.Spacing();
        using (ImRaii.Disabled(busy))
        {
            if (ImGui.Button("Refresh", new Vector2(bw, 0)))
            {
                plugin.RefreshProfiles();
                plugin.SelectedProfileIndex  = -1;
                plugin.SelectedTemplateIndex = -1;
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Reload your Customize+ profile and template list\nfrom the latest saved profiles.");
        }
    }

    private void DrawGrowthSpeed(Configuration config)
    {
        float timeSeconds = config.GrowthSpeed > 0f ? 1f / config.GrowthSpeed : 100f;
        UiHelpers.SectionLabel("Morph Time:",
            "How long the morph takes to complete, in seconds — lower is faster. " +
            "Ctrl+Click the slider to type an exact value.");
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (UiHelpers.GradientSlider("##GrowthSpeed", ref timeSeconds, 0.10f, 100f, "%.2f s", ImGuiSliderFlags.Logarithmic))
        {
            config.GrowthSpeed = timeSeconds > 0f ? 1f / timeSeconds : 0.01f;
            config.Save();
        }

        ImGui.TextDisabled($"Time to complete: {timeSeconds:F2}s   •   Speed: {config.GrowthSpeed:F2} range/sec");
    }

    private void DrawMorphTargetSelector(Configuration config)
    {
        bool busy = plugin.IsMorphing;

        UiHelpers.SectionLabel("Morph Target:",
            "Full Profile applies the whole selected profile as the destination, bones not present " +
            "in it reset to default. Template Overlay blends only the bones defined in the selected " +
            "Customize+ Template onto your current profile, every other bone stays untouched.");
        ImGui.Spacing();

        using (ImRaii.Disabled(busy))
        {
            int targetModeInt = (int)config.MorphTargetMode;
            if (UiHelpers.ButtonGroup("MorphTarget", UiHelpers.MorphTargetLabels, ref targetModeInt))
            {
                config.MorphTargetMode = (MorphTargetMode)targetModeInt;
                config.Save();
            }
        }
    }

    private void DrawMorphModeSelector(Configuration config)
    {
        bool busy = plugin.IsMorphing;

        UiHelpers.SectionLabel("Morph Loop:",
            "Simple morphs from your current profile to the destination once. Loop (x1) morphs " +
            "to the destination, then reverses back to the starting profile once. Loop (∞) " +
            "continuously ping-pongs between the starting and destination profiles until Reset is pressed.");
        ImGui.Spacing();

        using (ImRaii.Disabled(busy || plugin.IsPaused))
        {
            int modeInt = (int)config.MorphMode;
            if (UiHelpers.ButtonGroup("MorphMode", UiHelpers.MorphModeLabels, ref modeInt))
            {
                config.MorphMode = (MorphMode)modeInt;
                config.Save();
            }
        }
    }

    private void DrawEasingSelector(Configuration config)
    {
        bool busy = plugin.IsMorphing;

        UiHelpers.SectionLabel("Easing Curve:",
            "Linear is constant speed throughout. Ease In starts slow and accelerates toward the " +
            "destination. Ease Out starts fast and decelerates as it approaches the destination. " +
            "Ease In-Out starts slow, accelerates through the middle, then decelerates at the end.");
        ImGui.Spacing();

        using (ImRaii.Disabled(busy || plugin.IsPaused))
        {
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            var mode = config.EasingMode;
            var curveId = config.EasingCurveId;
            if (UiHelpers.EasingCombo("##Easing", config, ref mode, ref curveId))
            {
                config.EasingMode    = mode;
                config.EasingCurveId = curveId;
                config.Save();
            }
        }
    }

    private void DrawTargetSelector()
    {
        bool busy = plugin.IsMorphing;

        // ── Target selector ───────────────────────────────────────────────────
        var consentingPeers = plugin.ConsentingPeers;

        UiHelpers.SectionLabel("Target Actor:",
            "Who the morph is applied to. Self morphs your own character; picking a connected Sync " +
            "peer always sends them a consent request first, every single time, and the morph only " +
            "starts once they accept it.");
        ImGui.Spacing();

        string targetPreview = string.IsNullOrEmpty(plugin.TargetPlayerName)
            ? "Self"
            : plugin.TargetPlayerName;

        float targetComboWidth = ImGui.GetContentRegionAvail().X * 0.5f;
        ImGui.SetNextItemWidth(targetComboWidth);

        using (ImRaii.Disabled(busy || plugin.IsPaused))
        {
            if (ImGui.BeginCombo("##Target", targetPreview))
            {
                // Self option
                bool selfSelected = string.IsNullOrEmpty(plugin.TargetPlayerName);
                if (ImGui.Selectable("Self", selfSelected))
                    plugin.TargetPlayerName = null;
                if (selfSelected) ImGui.SetItemDefaultFocus();

                // Consenting peers. A peer already driven by the Sync tab's own target grid is
                // shown but not selectable — two independent sessions fighting over the same
                // character would be a mess, so pick one place or the other for that peer.
                if (consentingPeers.Count > 0)
                {
                    ImGui.Separator();
                    foreach (var peer in consentingPeers)
                    {
                        bool peerSelected = plugin.TargetPlayerName == peer;
                        bool inSyncGrid   = plugin.IsNetworkTargetActive(peer);
                        using (ImRaii.Disabled(inSyncGrid))
                        {
                            if (ImGui.Selectable(inSyncGrid ? $"{peer}  (Sync morph active)" : peer, peerSelected))
                                plugin.TargetPlayerName = peer;
                        }
                        if (inSyncGrid && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            ImGui.SetTooltip("Already being morphed from the Sync tab's target grid. " +
                                              "Reset it there first to target them here instead.");
                        if (peerSelected) ImGui.SetItemDefaultFocus();
                    }
                }
                else if (plugin.IsNetworkActive)
                {
                    ImGui.Separator();
                    ImGui.TextDisabled("No consenting peers connected.");
                }

                ImGui.EndCombo();
            }
        }

        if (!string.IsNullOrEmpty(plugin.TargetPlayerName))
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(new Vector4(0.9f, 0.8f, 0.2f, 1f),
                $"Morphing will be applied to {plugin.TargetPlayerName}'s character.");

            // Tell the user whether we know the target's original scaling — without it,
            // Reset can't restore their appearance on our screen (it would snap to unscaled).
            if (plugin.TargetBaseReady)
                ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.4f, 1f),
                    "Their original scaling is known — Reset will restore it.");
            else
                ImGui.TextColored(new Vector4(0.9f, 0.5f, 0.2f, 1f),
                    "Waiting for their scaling data… Reset may not restore it correctly yet.");
            ImGui.PopTextWrapPos();
        }
    }

    /// <summary>
    /// The progress bar and every morph control (Pause/Resume/Apply/Request/Waiting, Reverse,
    /// both Reset buttons) as one boxed unit via <see cref="UiHelpers.Card"/>. These are the
    /// buttons that actually drive a morph, so they're both grouped and sized bigger than the rest
    /// of the tab's buttons (Refresh, the preset grid, etc.) to read as the primary action surface.
    /// </summary>
    private void DrawMorphingCard(float innerWidth)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float gap   = ImGui.GetStyle().ItemSpacing.X;
        float h     = 34 * scale;
        bool  busy  = plugin.IsMorphing;

        // Same criteria the Apply button below enables on, used here for the status dot.
        bool hasDestination = plugin.Configuration.MorphTargetMode == MorphTargetMode.TemplateOverlay
            ? plugin.SelectedTemplateIndex >= 0 && plugin.SelectedTemplateIndex < plugin.SavedTemplates.Count
            : plugin.SelectedProfileIndex  >= 0 && plugin.SelectedProfileIndex  < plugin.SavedProfiles.Count;

        // ── Status + progress ────────────────────────────────────────────────
        // Theme.Warning reads as orange, too close to Theme.Danger at dot size to tell apart at a
        // glance, so the ready state uses a real yellow here instead (same tone as the completion
        // banner further down).
        if (busy)
            UiHelpers.StatusDot(Theme.Success, "Morphing in progress.");
        else if (!hasDestination)
            UiHelpers.StatusDot(Theme.Danger, "Missing selections, pick a destination before applying.");
        else
            UiHelpers.StatusDot(new Vector4(1f, 0.85f, 0.2f, 1f), "Ready, press Apply to start.");
        ImGui.SameLine();

        if (plugin.BoneAnimCount > 0 || busy)
        {
            ImGui.TextUnformatted($"Morphing... [{plugin.BoneAnimCount} bones]");
            float prog = plugin.Progress;
            UiHelpers.GradientProgressBar(prog, new Vector2(innerWidth, 18 * scale), $"{prog * 100f:F1}%");
        }
        else
        {
            ImGui.TextDisabled("No morph applied");
            UiHelpers.GradientProgressBar(0f, new Vector2(innerWidth, 18 * scale), "0.0%");
        }

        ImGui.Spacing();

        // ── Primary row: Pause / Resume / Apply / Request / Waiting [+ Reverse] ─
        bool done        = !busy && !plugin.IsPaused && plugin.BoneAnimCount > 0 && !plugin.IsGaugeMorphing;
        bool showReverse = busy || plugin.IsPaused || done;
        float primaryW   = showReverse ? (innerWidth - gap) * 0.65f : innerWidth;
        float reverseW   = innerWidth - gap - primaryW;

        if (busy)
        {
            if (ImGui.Button("Pause", new Vector2(primaryW, h)))
                plugin.PauseGrowth();
        }
        else if (plugin.IsPaused)
        {
            if (UiHelpers.PrimaryButton("Resume", new Vector2(primaryW, h)))
                plugin.ResumeGrowth();
        }
        else
        {
            bool canStart = hasDestination && plugin.Progress < 1f && !plugin.IsGaugeMorphing;

            string? target     = plugin.TargetPlayerName;
            bool    isTargeted = !string.IsNullOrEmpty(target) && plugin.IsNetworkActive;
            var     consent    = isTargeted ? plugin.GetConsentForTarget(target!) : ConsentStatus.Granted;

            if (consent == ConsentStatus.Pending)
            {
                using (ImRaii.Disabled(true))
                    ImGui.Button("Waiting…", new Vector2(primaryW, h));
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip($"Waiting for {target} to respond to your request…");
            }
            else
            {
                string label = consent == ConsentStatus.Idle && isTargeted ? "Request" : "Apply";
                using (ImRaii.Disabled(!canStart))
                {
                    if (UiHelpers.PrimaryButton(label, new Vector2(primaryW, h)))
                    {
                        if (consent == ConsentStatus.Idle && isTargeted)
                            plugin.RequestMorphConsent(target!);
                        else
                            plugin.StartGrowth();
                    }
                }

                if (consent == ConsentStatus.Idle && isTargeted && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip($"Send a consent request to {target} before applying the morph.");
            }
        }

        if (showReverse)
        {
            ImGui.SameLine();
            if (ImGui.Button("Reverse", new Vector2(reverseW, h)))
                plugin.ReverseGrowth();
        }

        ImGui.Spacing();

        // ── Reset row: always visible, split evenly ─────────────────────────────
        float resetW = (innerWidth - gap) / 2f;
        if (UiHelpers.DangerButton("Reset to Origin", new Vector2(resetW, h)))
        {
            plugin.ResetGrowth(toActiveProfile: false);
            onResetProfileCache();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Restores the Origin Profile (pinned, or whatever was active when the last morph began). Works at any time.");

        ImGui.SameLine();
        if (UiHelpers.DangerButton("Reset to Active", new Vector2(resetW, h)))
        {
            plugin.ResetGrowth(toActiveProfile: true);
            onResetProfileCache();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Restores whatever Customize+ profile is really active on your character right now. Works at any time.");

        // ── Completion banner ─────────────────────────────────────────────────
        if (done)
        {
            string? banner = plugin.CurrentMorphMode switch
            {
                MorphMode.Simple     when plugin.Progress >= 1f => "Morph complete! Press Reset to Origin or Reset to Active to restore your profile.",
                MorphMode.LoopSingle when plugin.Progress <= 0f => "Loop complete! You are back at the starting Profile. Press Reset to Origin or Reset to Active to clean up.",
                _                                               => null
            };

            if (banner != null)
            {
                ImGui.Spacing();
                ImGui.PushTextWrapPos(0f);
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), banner);
                ImGui.PopTextWrapPos();
            }
        }
    }

    private void DrawHistory()
    {
        var  config = plugin.Configuration;
        bool busy   = plugin.IsMorphing;
        float bw    = 90 * ImGuiHelpers.GlobalScale;

        if (config.RecentMorphs.Count == 0) return;

        // Section divider between the morph controls and the history list.
        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Recent Morphs")) return;
        ImGui.Spacing();

        // Applying an entry rewrites RecentMorphs (dedup + reinsert), so defer the call
        // until after the loop to avoid mutating the list we're enumerating.
        MorphPreset? toApply = null;
        foreach (var entry in config.RecentMorphs)
        {
            using (ImRaii.Disabled(busy || plugin.IsPaused))
            {
                if (ImGui.Button($"Apply##{entry.ProfileId}", new Vector2(bw, 0)))
                    toApply = entry;
            }
            ImGui.SameLine();
            ImGui.TextUnformatted($"{UiHelpers.PresetOriginLabel(entry)} → {entry.ProfileName}");
            ImGui.SameLine();
            ImGui.TextDisabled($"({entry.Speed:F2}/s  {UiHelpers.ModeShort(entry.Mode)}  {UiHelpers.EasingShort(entry.Easing, entry.EasingCurveId, config)}  {UiHelpers.TargetModeShort(entry.TargetMode)})");
        }

        if (toApply != null)
            plugin.ApplyMorphPreset(toApply);
    }

    private void DrawPresetsTab()
    {
        using var scroll = ImRaii.Child("##PlayerPresetScroll", new Vector2(0, 0), false);
        if (!scroll) return;

        var   config = plugin.Configuration;
        bool  busy   = plugin.IsMorphing;
        float bw     = 90 * ImGuiHelpers.GlobalScale;

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            $"Save up to {Configuration.PresetSlots} morph configurations for quick access. " +
            $"Use /bodyflux 1–{Configuration.PresetSlots} to apply from chat.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool canSave = config.MorphTargetMode == MorphTargetMode.TemplateOverlay
            ? plugin.SelectedTemplateIndex >= 0 && plugin.SelectedTemplateIndex < plugin.SavedTemplates.Count
            : plugin.SelectedProfileIndex  >= 0 && plugin.SelectedProfileIndex  < plugin.SavedProfiles.Count;

        for (int i = 0; i < Configuration.PresetSlots; i++)
        {
            var  preset    = i < config.Presets.Count ? config.Presets[i] : null;
            bool hasPreset = preset != null;

            using (ImRaii.Disabled(busy || !hasPreset))
            {
                if (UiHelpers.PrimaryButton($"Apply##{i}", new Vector2(bw, 0)))
                    plugin.ApplyMorphPreset(preset!);
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(!canSave))
            {
                if (ImGui.Button($"Save##{i}", new Vector2(bw, 0)))
                    plugin.SavePreset(i);
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(!hasPreset))
            {
                if (UiHelpers.DangerButton($"Clear##{i}", new Vector2(bw, 0)))
                    plugin.ClearPreset(i);
            }

            ImGui.SameLine();
            ImGui.TextUnformatted($"{i + 1}.");
            ImGui.SameLine();

            if (hasPreset)
            {
                ImGui.TextUnformatted($"{UiHelpers.PresetOriginLabel(preset!)} → {preset!.ProfileName}");
                ImGui.SameLine();
                ImGui.TextDisabled($"({preset.Speed:F2}/s  {UiHelpers.ModeShort(preset.Mode)}  {UiHelpers.EasingShort(preset.Easing, preset.EasingCurveId, config)}  {UiHelpers.TargetModeShort(preset.TargetMode)})");
            }
            else
            {
                ImGui.TextDisabled("— empty —");
            }
        }
    }

    private void DrawSequencesSubTab()
    {
        using var scroll = ImRaii.Child("##PlayerSeqScroll", new Vector2(0, 0), false);
        if (!scroll) return;

        var   config = plugin.Configuration;
        float scale  = ImGuiHelpers.GlobalScale;
        float bw     = 90 * scale;
        bool  busy   = plugin.IsMorphing || plugin.IsPaused; // a morph is running/paused → lock editing

        // Keep _seqPlayingIndex pointing at the sequence that is playing, paused, or resting at
        // its final destination. Clear it once nothing is active so the footer disappears.
        bool restingMorph = plugin.BoneAnimCount > 0 && plugin.MorphTargetIndex == 0;
        if (!plugin.IsSequenceRunning && !plugin.IsPaused && !restingMorph)
            _seqPlayingIndex = -1;
        bool seqActive = _seqPlayingIndex >= 0;

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            "Chain several morphs into an automatic A→B→C sequence. Each step morphs from the " +
            "previous step's result, with its own speed and easing. Steps always run once (Simple).");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        int playRequest = seqList.Draw(
            config.Sequences, ref _seqPlayingIndex, ref _seqAddFilter, ref _seqAddMode,
            config.GrowthSpeed, busy, seqActive, playAllowed: true, null,
            onReset: () => plugin.ResetGrowth(toActiveProfile: false),
            onResetActive: () => plugin.ResetGrowth(toActiveProfile: true),
            bw, scale);

        if (playRequest >= 0 && plugin.StartSequence(config.Sequences[playRequest]))
            _seqPlayingIndex = playRequest;

        if (!seqActive) return;
        DrawSequenceFooter(bw, scale, "Sequence complete — press Reset to Origin or Reset to Active to restore your profile.");
    }

    private void DrawGaugeBindSubTab()
    {
        var   config = plugin.Configuration;
        float scale  = ImGuiHelpers.GlobalScale;
        float bw     = 90 * scale;
        bool  cpOk   = plugin.IsCustomizePlusAvailable;
        bool  bound  = plugin.IsGaugeMorphing;

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            "Bind your morph to a live resource (MP or a bar-type job gauge). Your body tracks the " +
            "gauge in real time — 50% gauge = halfway to the destination profile, 100% = full morph.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Requirement / availability ────────────────────────────────────────
        if (!cpOk)
        {
            ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1f),
                "Customize+ not detected — please install and enable it.");
            return;
        }

        // ── Origin (folds in what's currently active, same as the Single tab) ───
        using (ImRaii.Disabled(bound))
            DrawOriginProfilePicker(config);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Target mode selector ──────────────────────────────────────────────
        UiHelpers.SectionLabel("Morph Target:", null);
        ImGui.Spacing();

        using (ImRaii.Disabled(bound))
        {
            int targetModeInt = (int)config.MorphTargetMode;
            if (UiHelpers.ButtonGroup("GaugeMorphTarget", UiHelpers.MorphTargetLabels, ref targetModeInt))
            {
                config.MorphTargetMode = (MorphTargetMode)targetModeInt;
                config.Save();
            }
        }

        bool overlay = config.MorphTargetMode == MorphTargetMode.TemplateOverlay;

        ImGui.Spacing();

        // ── Destination ────────────────────────────────────────────────────────
        UiHelpers.SectionLabel(overlay ? "Overlay Template:" : "Destination Profile:", null);
        ImGui.SetNextItemWidth(-1);

        using (ImRaii.Disabled(bound))
        {
            if (overlay)
            {
                var names = plugin.SavedTemplates.Select(t => t.Name).ToList();
                int  idx  = plugin.SelectedTemplateIndex;
                UiHelpers.DrawFilterableCombo("##GaugeDestTemplate", "##GaugeTemplateFilter", names,
                    ref idx, ref _profileFilter, "— select a template —");
                plugin.SelectedTemplateIndex = idx;
            }
            else
            {
                var names = plugin.SavedProfiles.Select(p => p.Name).ToList();
                int  idx  = plugin.SelectedProfileIndex;
                UiHelpers.DrawFilterableCombo("##GaugeDestProfile", "##GaugeProfileFilter", names,
                    ref idx, ref _profileFilter, "— select a profile —");
                plugin.SelectedProfileIndex = idx;
            }
        }

        ImGui.Spacing();

        // ── Resource selector ─────────────────────────────────────────────────
        UiHelpers.SectionLabel("Resource:", null);
        ImGui.SetNextItemWidth(-1);

        var resource = config.LastGaugeResource;
        using (ImRaii.Disabled(bound))
        {
            if (ImGui.BeginCombo("##GaugeResource", GaugeReader.Label(resource)))
            {
                foreach (var r in Enum.GetValues<GaugeResource>())
                {
                    bool selected = r == resource;
                    if (ImGui.Selectable(GaugeReader.Label(r), selected))
                    {
                        config.LastGaugeResource = r;
                        config.Save();
                    }
                    if (selected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }

        ImGui.Spacing();

        // ── Invert ────────────────────────────────────────────────────────────
        using (ImRaii.Disabled(bound))
        {
            bool invert = config.LastGaugeInvert;
            if (ImGui.Checkbox("Invert (shrink as the gauge fills)", ref invert))
            {
                config.LastGaugeInvert = invert;
                config.Save();
            }
        }

        // ── Live fill preview ─────────────────────────────────────────────────
        ImGui.Spacing();
        var fill = plugin.ReadGaugeFill(config.LastGaugeResource);
        if (fill is { } f)
        {
            ImGui.TextDisabled("Current fill:");
            ImGui.SameLine();
            UiHelpers.GradientProgressBar(f, new Vector2(-1, 16 * scale), $"{f * 100f:F0}%");
        }
        else
        {
            ImGui.TextDisabled("Current fill: — (not on the matching job / logged out)");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Bind / Unbind ─────────────────────────────────────────────────────
        if (bound)
        {
            if (UiHelpers.DangerButton("Unbind", new Vector2(bw, 0)))
            {
                plugin.StopGaugeMorph();
                onResetProfileCache();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.4f, 1f),
                $"Bound to {GaugeReader.Label(config.LastGaugeResource)} — tracking live.");
        }
        else
        {
            // Gauge morphs need a clean, idle self-session and can't run in GPose.
            bool hasDestination = overlay
                ? plugin.SelectedTemplateIndex >= 0 && plugin.SelectedTemplateIndex < plugin.SavedTemplates.Count
                : plugin.SelectedProfileIndex  >= 0 && plugin.SelectedProfileIndex  < plugin.SavedProfiles.Count;
            bool idle       = !plugin.IsMorphing && !plugin.IsPaused && plugin.BoneAnimCount == 0;
            bool canBind    = hasDestination && idle && !plugin.IsInGPose;

            using (ImRaii.Disabled(!canBind))
            {
                if (UiHelpers.PrimaryButton("Bind", new Vector2(bw, 0)))
                    plugin.StartGaugeMorph(config.LastGaugeResource, config.LastGaugeInvert);
            }

            if (!hasDestination)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(overlay ? "Select an overlay template first." : "Select a destination profile first.");
            }
            else if (plugin.IsInGPose)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.9f, 0.5f, 0.2f, 1f), "Unavailable in GPose.");
            }
            else if (!idle)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.9f, 0.5f, 0.2f, 1f),
                    "Reset the active time morph first.");
            }
        }

        // ── Constraints / caveats ─────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            "• Local player only — gauge data isn't available for other actors or GPose clones.\n" +
            "• In a Sync group, peers see your body track the gauge live.\n" +
            "• MP is full out of combat, so an MP-linked morph sits at 100% (or 0% inverted) until you cast.");
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// Playback footer for the player sequence: status line, progress bar, Pause/Resume, and the
    /// Reset row (always on its own line below, same layout as the Single tab's controls).
    /// </summary>
    private void DrawSequenceFooter(float bw, float scale, string completeText)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool running = plugin.IsSequenceRunning && plugin.IsMorphing;
        bool paused  = plugin.IsPaused;

        if (plugin.IsSequenceRunning)
            ImGui.TextUnformatted($"Playing step {plugin.SequenceStep} / {plugin.SequenceStepCount}");
        else
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), completeText);

        float prog = plugin.Progress;
        UiHelpers.GradientProgressBar(prog, new Vector2(-1, 18 * scale), $"{prog * 100f:F0}%");

        ImGui.Spacing();

        if (running)
        {
            if (ImGui.Button("Pause##Seq", new Vector2(bw, 0)))
                plugin.PauseGrowth();
        }
        else if (paused)
        {
            if (UiHelpers.PrimaryButton("Resume##Seq", new Vector2(bw, 0)))
                plugin.ResumeGrowth();
        }

        // ── Reset row: always on its own line below Pause/Resume ───────────────
        if (UiHelpers.DangerButton("Reset to Origin##Seq", new Vector2(bw * 1.4f, 0)))
            plugin.ResetGrowth(toActiveProfile: false);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Restores the Origin Profile (pinned, or whatever was active when the sequence began). Also stops the sequence.");

        ImGui.SameLine();
        if (UiHelpers.DangerButton("Reset to Active##Seq", new Vector2(bw * 1.4f, 0)))
            plugin.ResetGrowth(toActiveProfile: true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Restores whatever Customize+ profile is really active on your character right now. Also stops the sequence.");
    }
}
