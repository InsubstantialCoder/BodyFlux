using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using BodyFlux.Morph;

namespace BodyFlux.Windows.Tabs;

/// <summary>
/// Renders the "Brio" tab and its sub-tabs (Single, Presets, Sequences, Multi): per-actor morph
/// config, the active-morphs list, the multi-actor group editor, Brio presets and history.
/// Only meaningful in GPose; the parent gates interactivity.
/// </summary>
public sealed class BrioTabView
{
    private readonly Plugin            plugin;
    private readonly SequenceListView  seqList;
    private readonly FileDialogManager fileDialog; // shared with MainWindow, drawn at the top level

    private string          _profileFilter   = ""; // Single-tab destination picker filter
    private string          _seqAddFilter    = ""; // Sequences "Add step" picker filter
    private MorphTargetMode _seqAddMode      = MorphTargetMode.FullProfile; // Sequences "Add step" target mode

    // Short target labels for the Multi tab's per-actor editor, which keeps its ButtonGroup row on
    // the same line as its SectionLabel to stay compact inside an indented CollapsingHeader, where
    // the full-length UiHelpers.MorphTargetLabels text wouldn't fit. Mode uses UiHelpers.MorphModeLabels
    // directly now that "Loop (x1)"/"Loop (∞)" are short enough to fit there too.
    private static readonly string[] GroupTargetLabels = ["Full", "Overlay"];

    // ── Group (Multi) sub-tab state ───────────────────────────────────────────
    // One configurable entry per actor; "Apply All" morphs them simultaneously. In-memory only.
    private sealed class GroupEntry
    {
        public ushort     ActorIndex;
        public int        DestIndex     = -1;  // index into SavedProfiles
        public int        TemplateIndex = -1;  // index into SavedTemplates (used when TargetMode == TemplateOverlay)
        public string?    OriginMcdfJson;      // captured MCDF JSON (null = permanent/identity)
        public string     OriginMcdfLabel = "";
        public float      Speed  = 0.5f;
        public MorphMode  Mode   = MorphMode.Simple;
        public EasingMode Easing = EasingMode.Linear;
        public Guid?      EasingCurveId;
        public MorphTargetMode TargetMode = MorphTargetMode.FullProfile;
    }
    private readonly List<GroupEntry> _group = [];
    private string _groupProfileFilter = ""; // shared filter for the per-row destination pickers

    public BrioTabView(Plugin plugin, SequenceListView seqList, FileDialogManager fileDialog)
    {
        this.plugin     = plugin;
        this.seqList    = seqList;
        this.fileDialog = fileDialog;
    }

    public void Draw()
    {
        using var subTabs = ImRaii.TabBar("##BrioSubTabs");
        if (!subTabs) return;

        using (var tab = ImRaii.TabItem("Single##BrioSub"))
            if (tab) DrawMorphSubTab();

        using (var tab = ImRaii.TabItem("Presets##BrioSub"))
            if (tab) DrawPresetsTab();

        using (var tab = ImRaii.TabItem("Sequences##BrioSub"))
            if (tab) DrawSequencesSubTab();

        using (var tab = ImRaii.TabItem("Multi##BrioSub"))
            if (tab) DrawGroupSubTab();
    }

    private void DrawMorphSubTab()
    {
        using var scroll = ImRaii.Child("##BrioMorphScroll", new Vector2(0, 0), false);
        if (!scroll) return;

        float scale  = ImGuiHelpers.GlobalScale;
        var   config = plugin.Configuration;
        var   actors = plugin.GetGPoseActors();

        ImGui.Spacing();

        // ── Actor selector: its own full-width row above the columns, since Player's Single
        //    tab has nothing equivalent to balance it against on either side.
        DrawActorSelector(actors);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Spacing();

        // ── Two columns, same split as Player's Single tab: origin/destination/target on the
        //    left, speed/mode/easing on the right. A vertical border plus extra cell padding
        //    keeps the two groups of controls from reading as one continuous block.
        using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding,
                   new Vector2(14 * ImGuiHelpers.GlobalScale, ImGui.GetStyle().CellPadding.Y)))
        using (var table = ImRaii.Table("##BrioColumns", 2, ImGuiTableFlags.BordersInnerV))
        {
            if (table)
            {
                ImGui.TableSetupColumn("##left",  ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("##right", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                DrawBrioLeftColumn(config, actors);

                ImGui.TableSetColumnIndex(1);
                DrawBrioRightColumn(config);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Spacing();

        // ── Apply (adds/updates the selected actor's morph) ───────────────────
        bool hasDestination = config.MorphTargetMode == MorphTargetMode.TemplateOverlay
            ? plugin.SelectedBrioTemplateIndex >= 0 && plugin.SelectedBrioTemplateIndex < plugin.SavedTemplates.Count
            : plugin.SelectedBrioDestIndex     >= 0 && plugin.SelectedBrioDestIndex     < plugin.SavedProfiles.Count;
        bool canApply = plugin.SelectedBrioActorIndex >= 0 && hasDestination;
        using (ImRaii.Disabled(!canApply))
        {
            if (UiHelpers.PrimaryButton("Apply##Brio", new Vector2(ImGui.GetContentRegionAvail().X, 34 * scale)))
                plugin.StartBrioMorph();
        }
        if (canApply && plugin.IsBrioActorActive((ushort)plugin.SelectedBrioActorIndex)
            && ImGui.IsItemHovered())
            ImGui.SetTooltip("This actor already has an active morph, Apply replaces it.");

        // ── Active morphs list ────────────────────────────────────────────────
        DrawActiveMorphs(actors, scale);

        DrawHistory();
    }

    private void DrawActorSelector(List<(ushort Index, string Name)> actors)
    {
        UiHelpers.SectionLabel("GPose Actor:",
            "Configure an actor and destination below, then press Apply to morph it. Multiple " +
            "actors can morph at once, each appears in the Active Morphs list with its own " +
            "controls. All actors reset automatically when you exit GPose.");
        ImGui.Spacing();

        string actorPreview = "— select an actor —";
        if (plugin.SelectedBrioActorIndex >= 0)
        {
            var found = actors.Find(a => a.Index == (ushort)plugin.SelectedBrioActorIndex);
            actorPreview = found.Name != null
                ? $"[{found.Index}] {found.Name}"
                : $"Actor {plugin.SelectedBrioActorIndex} (not in scene)";
        }

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.5f);
        if (ImGui.BeginCombo("##ActorSelect", actorPreview))
        {
            if (actors.Count == 0)
            {
                ImGui.TextDisabled("No GPose actors found. Create clones using Brio.");
            }
            else
            {
                foreach (var (idx, name) in actors)
                {
                    bool sel = plugin.SelectedBrioActorIndex == idx;
                    string tag = plugin.IsBrioActorActive(idx) ? "  (morphing)" : "";
                    if (ImGui.Selectable($"[{idx}] {name}{tag}##actor{idx}", sel))
                        plugin.SelectedBrioActorIndex = idx;
                    if (sel) ImGui.SetItemDefaultFocus();
                }
            }
            ImGui.EndCombo();
        }
    }

    private void DrawBrioLeftColumn(Configuration config, List<(ushort Index, string Name)> actors)
    {
        // ── Origin scaling (MCDF) ─────────────────────────────────────────────
        UiHelpers.SectionLabel("Origin Scaling (MCDF):",
            "Customize+ can't read a clone's applied scaling, so load the MCDF that was applied " +
            "to it. It becomes the starting point for the next Apply and is restored on that " +
            "actor's Reset. Without it, the morph starts from an unscaled body.");
        ImGui.Spacing();

        float bw = 90 * ImGuiHelpers.GlobalScale;
        if (ImGui.Button("Load MCDF...", new Vector2(bw + 30 * ImGuiHelpers.GlobalScale, 0)))
        {
            var startPath = plugin.GetBrioLastMcdfFolder() ?? "";
            fileDialog.OpenFileDialog(
                "Select the origin MCDF", "MCDF files{.mcdf}",
                (ok, paths) =>
                {
                    if (ok && paths.Count > 0)
                        plugin.LoadBrioOriginMcdf(paths[0]);
                },
                1, startPath, false);
        }

        if (plugin.BrioOriginLoaded)
        {
            ImGui.SameLine();
            if (ImGui.Button("Clear##BrioOrigin", new Vector2(bw, 0)))
                plugin.ClearBrioOrigin();
        }

        ImGui.Spacing();
        if (plugin.BrioOriginLoaded)
            ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.4f, 1f), $"Origin: {plugin.BrioOriginLabel}");
        else if (plugin.BrioOriginLabel.Length > 0) // an error was reported
            ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1f), plugin.BrioOriginLabel);
        else
            ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "No MCDF loaded, morph will start unscaled.");

        ImGui.Spacing();
        ImGui.Spacing();

        // ── Target mode selector ──────────────────────────────────────────────
        // Drawn before Destination since it determines whether that picker lists profiles
        // or templates.
        UiHelpers.SectionLabel("Morph Target:",
            "Full Profile applies the whole selected profile as the destination, bones not present " +
            "in it reset to default. Template Overlay blends only the bones defined in the selected " +
            "Customize+ Template onto the actor's current profile, every other bone stays untouched.");
        ImGui.Spacing();

        int targetModeInt = (int)config.MorphTargetMode;
        if (UiHelpers.ButtonGroup("BrioMorphTarget", UiHelpers.MorphTargetLabels, ref targetModeInt))
        {
            config.MorphTargetMode = (MorphTargetMode)targetModeInt;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Spacing();

        // ── Destination selector ──────────────────────────────────────────────
        bool overlay = config.MorphTargetMode == MorphTargetMode.TemplateOverlay;
        UiHelpers.SectionLabel(overlay ? "Overlay Template:" : "Destination Profile:",
            overlay
                ? "The Customize+ Template the morph plays toward. Only templates attached to at " +
                  "least one profile show up here."
                : "The saved Customize+ profile the morph plays toward for this actor.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (overlay)
        {
            var names = plugin.SavedTemplates.Select(t => t.Name).ToList();
            int  idx  = plugin.SelectedBrioTemplateIndex;
            UiHelpers.DrawFilterableCombo("##BrioDestTemplate", "##BrioTemplateFilter", names,
                ref idx, ref _profileFilter, "— select a template —");
            plugin.SelectedBrioTemplateIndex = idx;
        }
        else
        {
            var names = plugin.SavedProfiles.Select(p => p.Name).ToList();
            int  idx  = plugin.SelectedBrioDestIndex;
            UiHelpers.DrawFilterableCombo("##BrioDestProfile", "##BrioProfileFilter", names,
                ref idx, ref _profileFilter, "— select a profile —");
            plugin.SelectedBrioDestIndex = idx;
        }

        ImGui.Spacing();
        if (ImGui.Button("Refresh##BrioRefresh", new Vector2(bw, 0)))
        {
            plugin.RefreshProfiles();
            plugin.SelectedBrioDestIndex     = -1;
            plugin.SelectedBrioTemplateIndex = -1;
        }
    }

    private void DrawBrioRightColumn(Configuration config)
    {
        // ── Speed ─────────────────────────────────────────────────────────────
        float speed = config.BrioGrowthSpeed;
        UiHelpers.SectionLabel("Growth Speed:",
            "How fast the morph plays, in Customize+ range units per second, higher is faster. " +
            "\"Time to complete\" below shows what that works out to at the current speed.");
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (UiHelpers.GradientSlider("##BrioGrowthSpeed", ref speed, 0.01f, 1f, "%.2f range/sec"))
        {
            config.BrioGrowthSpeed = speed;
            config.Save();
        }

        float secs = speed > 0f ? 1f / speed : float.PositiveInfinity;
        ImGui.TextDisabled($"Time to complete: {secs:F1}s");

        ImGui.Spacing();
        ImGui.Spacing();

        // ── Mode selector ─────────────────────────────────────────────────────
        UiHelpers.SectionLabel("Morph Mode:",
            "Simple morphs from the actor's current profile to the destination once. Loop (x1) " +
            "morphs there and reverses back once. Loop (∞) continuously ping-pongs between " +
            "the starting and destination profiles until Reset is pressed.");
        ImGui.Spacing();

        int modeInt = (int)config.MorphMode;
        if (UiHelpers.ButtonGroup("BrioMorphMode", UiHelpers.MorphModeLabels, ref modeInt))
        {
            config.MorphMode = (MorphMode)modeInt;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Spacing();

        // ── Easing Curve ──────────────────────────────────────────────────────
        UiHelpers.SectionLabel("Easing Curve:",
            "Linear is constant speed throughout. Ease In starts slow and accelerates toward the " +
            "destination. Ease Out starts fast and decelerates as it approaches the destination. " +
            "Ease In-Out starts slow, accelerates through the middle, then decelerates at the end.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        var brioMode = config.BrioEasingMode;
        var brioCurveId = config.BrioEasingCurveId;
        if (UiHelpers.EasingCombo("##BrioEasing", config, ref brioMode, ref brioCurveId))
        {
            config.BrioEasingMode    = brioMode;
            config.BrioEasingCurveId = brioCurveId;
            config.Save();
        }
    }

    /// <summary>Renders one card per active Brio actor morph, each with its own progress + controls.</summary>
    private void DrawActiveMorphs(List<(ushort Index, string Name)> actors, float scale)
    {
        var active = plugin.GetActiveBrioMorphs();

        UiHelpers.SectionLabel($"Active Morphs ({active.Count}):", null);
        ImGui.Spacing();

        if (active.Count == 0)
        {
            ImGui.TextDisabled("No actors are being morphed. Configure above and press Apply.");
            return;
        }

        float h = 30 * scale;
        foreach (var m in active)
        {
            ImGui.PushID(m.Index);
            UiHelpers.Card(innerWidth =>
            {
                var found = actors.Find(a => a.Index == m.Index);
                string name = found.Name ?? "(not in scene)";
                ImGui.TextUnformatted($"[{m.Index}] {name}");
                if (m.SeqRunning)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"(sequence step {m.SeqStep}/{m.SeqStepCount})");
                }

                UiHelpers.GradientProgressBar(m.Progress, new Vector2(innerWidth, 16 * scale),
                                               $"{m.Progress * 100f:F0}%  ({m.BoneCount} bones)");
                ImGui.Spacing();

                // Reverse only applies to single morphs (sequences chain forward only).
                bool  showReverse = !m.SeqRunning && (m.IsMorphing || m.IsPaused || m.IsFinished);
                float gap         = ImGui.GetStyle().ItemSpacing.X;
                float primaryW    = showReverse ? (innerWidth - gap * 2f) / 3f : innerWidth - gap;
                float reverseW    = primaryW;
                float resetW      = innerWidth - gap * 2f - primaryW - reverseW;
                if (!showReverse) resetW = innerWidth - gap - primaryW;

                if (m.IsMorphing)
                {
                    if (UiHelpers.PrimaryButton("Pause", new Vector2(primaryW, h))) plugin.PauseBrioActor(m.Index);
                }
                else if (m.IsPaused)
                {
                    if (UiHelpers.PrimaryButton("Resume", new Vector2(primaryW, h))) plugin.ResumeBrioActor(m.Index);
                }
                else
                {
                    ImGui.Dummy(new Vector2(primaryW, h)); // keeps Reverse/Reset aligned when there's no Pause/Resume
                }

                if (showReverse)
                {
                    ImGui.SameLine();
                    if (ImGui.Button("Reverse", new Vector2(reverseW, h))) plugin.ReverseBrioActor(m.Index);
                }

                ImGui.SameLine();
                if (UiHelpers.DangerButton("Reset", new Vector2(resetW, h))) plugin.ResetBrioActor(m.Index);
            });
            ImGui.Spacing();
            ImGui.PopID();
        }
    }

    private void DrawSequencesSubTab()
    {
        using var scroll = ImRaii.Child("##BrioSeqScroll", new Vector2(0, 0), false);
        if (!scroll) return;

        var   config = plugin.Configuration;
        float scale  = ImGuiHelpers.GlobalScale;
        float bw     = 90 * scale;

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            "Chain several morphs into an automatic A→B→C sequence and Play it on a GPose actor. " +
            "The target actor and MCDF origin come from your selection in the Single tab; live " +
            "playback and Stop appear there in the Active Morphs list. Several actors can run " +
            "sequences at once.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Target info (from the Single tab selection) ────────────────────────
        bool   actorSelected = plugin.SelectedBrioActorIndex >= 0;
        string actorName     = "— none —";
        if (actorSelected)
        {
            var found = plugin.GetGPoseActors().Find(a => a.Index == (ushort)plugin.SelectedBrioActorIndex);
            actorName = found.Name ?? "(not in scene)";
        }

        UiHelpers.SectionLabel("Target actor:", null);
        ImGui.SameLine();
        ImGui.TextColored(actorSelected ? new Vector4(0.4f, 0.9f, 0.6f, 1f) : new Vector4(0.9f, 0.7f, 0.2f, 1f),
            actorName);

        UiHelpers.SectionLabel("Origin MCDF:", null);
        ImGui.SameLine();
        ImGui.TextDisabled(plugin.BrioOriginLoaded ? plugin.BrioOriginLabel : "none — starts from permanent/identity");

        ImGui.TextDisabled("Choose the actor and load an MCDF in the Single tab before playing.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Library + Play. Playback is per-actor, so this tab keeps no single playing-index or
        // footer — Play just launches the sequence onto the selected actor.
        int dummyPlaying = -1;
        int playRequest = seqList.Draw(
            config.BrioSequences, ref dummyPlaying, ref _seqAddFilter, ref _seqAddMode,
            config.BrioGrowthSpeed, busy: false, seqActive: false,
            playAllowed: actorSelected, "Select an actor in the Single tab first.",
            onReset: ResetSequenceTarget, onResetActive: null, bw, scale);

        if (playRequest >= 0)
            plugin.StartBrioSequence(config.BrioSequences[playRequest]);
    }

    /// <summary>Reset target for a Brio sequence's Reset button: the selected actor, or all actors
    /// if none is selected.</summary>
    private void ResetSequenceTarget()
    {
        if (plugin.SelectedBrioActorIndex >= 0)
            plugin.ResetBrioActor((ushort)plugin.SelectedBrioActorIndex);
        else
            plugin.ResetAllBrio();
    }

    private void DrawGroupSubTab()
    {
        using var scroll = ImRaii.Child("##BrioGroupScroll", new Vector2(0, 0), false);
        if (!scroll) return;

        var   config = plugin.Configuration;
        float scale  = ImGuiHelpers.GlobalScale;
        float bw     = 90 * scale;
        var   actors = plugin.GetGPoseActors();

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            "Configure several GPose actors — each with its own MCDF origin, destination, speed, " +
            "mode and easing — then press Apply All to morph them at the same time. The group " +
            "controls act on every active Brio morph.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Add actor ─────────────────────────────────────────────────────────
        UiHelpers.SectionLabel("Add actor:", null);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginCombo("##GroupAddActor", "— select an actor to add —"))
        {
            bool any = false;
            foreach (var (idx, name) in actors)
            {
                if (_group.Exists(g => g.ActorIndex == idx)) continue; // already in the group
                any = true;
                if (ImGui.Selectable($"[{idx}] {name}##gadd{idx}"))
                    _group.Add(new GroupEntry
                    {
                        ActorIndex = idx,
                        Speed      = config.BrioGrowthSpeed,
                        Mode       = config.MorphMode,
                        Easing     = config.BrioEasingMode,
                    });
            }
            if (!any)
                ImGui.TextDisabled(actors.Count == 0 ? "No GPose actors found." : "All actors already added.");
            ImGui.EndCombo();
        }

        ImGui.Spacing();
        if (_group.Count == 0)
            ImGui.TextDisabled("No actors configured yet — add one above.");

        // ── Per-actor config rows ─────────────────────────────────────────────
        int removeIdx = -1;
        for (int i = 0; i < _group.Count; i++)
        {
            var g = _group[i];
            ImGui.PushID(i);

            var    found   = actors.Find(a => a.Index == g.ActorIndex);
            string name    = found.Name ?? "(not in scene)";
            bool   active  = plugin.IsBrioActorActive(g.ActorIndex);

            string header = $"[{g.ActorIndex}] {name}" + (active ? "  (morphing)" : "");
            if (ImGui.CollapsingHeader($"{header}##grphdr{i}", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent(12 * scale);
                DrawGroupEntryEditor(g, i, bw, scale, ref removeIdx);
                ImGui.Unindent(12 * scale);
            }

            ImGui.PopID();
        }
        if (removeIdx >= 0)
        {
            plugin.ResetBrioActor(_group[removeIdx].ActorIndex); // stop it if currently morphing
            _group.RemoveAt(removeIdx);
        }

        // ── Group controls ────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        int applyable = _group.FindAll(g =>
            (g.TargetMode == MorphTargetMode.TemplateOverlay
                ? g.TemplateIndex >= 0 && g.TemplateIndex < plugin.SavedTemplates.Count
                : g.DestIndex     >= 0 && g.DestIndex     < plugin.SavedProfiles.Count)
            && actors.Exists(a => a.Index == g.ActorIndex)).Count;

        using (ImRaii.Disabled(applyable == 0))
        {
            if (UiHelpers.PrimaryButton($"Apply All ({applyable})", new Vector2(bw + 40 * scale, 0)))
                ApplyGroup();
        }

        ImGui.Spacing();
        if (ImGui.Button("Pause All",   new Vector2(bw, 0))) plugin.PauseAllBrio();
        ImGui.SameLine();
        if (UiHelpers.PrimaryButton("Resume All",  new Vector2(bw, 0))) plugin.ResumeAllBrio();
        ImGui.SameLine();
        if (ImGui.Button("Reverse All", new Vector2(bw, 0))) plugin.ReverseAllBrio();
        ImGui.SameLine();
        if (UiHelpers.DangerButton("Reset All",   new Vector2(bw, 0))) plugin.ResetAllBrio();
    }

    /// <summary>
    /// Morphs every configured group entry at once (the Multi tab's "Apply All"). Skips entries with
    /// no destination or whose actor has left the scene. Also invoked by the "Apply Multi" keybind.
    /// </summary>
    public void ApplyGroup()
    {
        var actors = plugin.GetGPoseActors();
        foreach (var g in _group)
        {
            if (!actors.Exists(a => a.Index == g.ActorIndex)) continue; // gone from scene

            Guid destId; Guid? templateOwnerProfileId = null;
            if (g.TargetMode == MorphTargetMode.TemplateOverlay)
            {
                if (g.TemplateIndex < 0 || g.TemplateIndex >= plugin.SavedTemplates.Count) continue;
                var t = plugin.SavedTemplates[g.TemplateIndex];
                destId = t.Id; templateOwnerProfileId = t.OwnerProfileId;
            }
            else
            {
                if (g.DestIndex < 0 || g.DestIndex >= plugin.SavedProfiles.Count) continue;
                destId = plugin.SavedProfiles[g.DestIndex].Id;
            }

            plugin.StartBrioMorphFor(g.ActorIndex, destId, g.OriginMcdfJson, g.Speed, g.Mode, g.Easing,
                g.TargetMode, templateOwnerProfileId, g.EasingCurveId);
        }
    }

    /// <summary>Draws the config editor for one group entry (destination, MCDF, speed, mode, easing).</summary>
    private void DrawGroupEntryEditor(GroupEntry g, int index, float bw, float scale, ref int removeIdx)
    {
        // ── Target mode ────────────────────────────────────────────────────────
        UiHelpers.SectionLabel("Target:", null);
        ImGui.SameLine();
        int grpTargetModeInt = (int)g.TargetMode;
        if (UiHelpers.ButtonGroup("grptgt", GroupTargetLabels, ref grpTargetModeInt))
            g.TargetMode = (MorphTargetMode)grpTargetModeInt;

        // ── Destination ───────────────────────────────────────────────────────
        bool grpOverlay = g.TargetMode == MorphTargetMode.TemplateOverlay;
        UiHelpers.SectionLabel(grpOverlay ? "Overlay:" : "Destination:", null);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (grpOverlay)
        {
            var names = plugin.SavedTemplates.Select(t => t.Name).ToList();
            int  idx  = g.TemplateIndex;
            UiHelpers.DrawFilterableCombo("##grpdest", "##grpdestfilter", names,
                ref idx, ref _groupProfileFilter, "— select a template —");
            g.TemplateIndex = idx;
        }
        else
        {
            var names = plugin.SavedProfiles.Select(p => p.Name).ToList();
            int  idx  = g.DestIndex;
            UiHelpers.DrawFilterableCombo("##grpdest", "##grpdestfilter", names,
                ref idx, ref _groupProfileFilter, "— select a profile —");
            g.DestIndex = idx;
        }

        // ── MCDF origin ───────────────────────────────────────────────────────
        if (ImGui.Button("Load MCDF...##grp"))
        {
            var startPath = plugin.GetBrioLastMcdfFolder() ?? "";
            fileDialog.OpenFileDialog(
                "Select the origin MCDF", "MCDF files{.mcdf}",
                (ok, paths) =>
                {
                    if (ok && paths.Count > 0)
                    {
                        var (rok, json, label) = plugin.ReadMcdfProfile(paths[0]);
                        g.OriginMcdfJson  = rok ? json : null;
                        g.OriginMcdfLabel = label;
                    }
                },
                1, startPath, false);
        }
        if (g.OriginMcdfJson != null)
        {
            ImGui.SameLine();
            if (UiHelpers.DangerButton("Clear##grpmcdf", Vector2.Zero)) { g.OriginMcdfJson = null; g.OriginMcdfLabel = ""; }
        }
        ImGui.SameLine();
        if (g.OriginMcdfJson != null)
            ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.4f, 1f), g.OriginMcdfLabel);
        else if (g.OriginMcdfLabel.Length > 0) // an error was reported
            ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1f), g.OriginMcdfLabel);
        else
            ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "no MCDF — starts unscaled/permanent");

        // ── Speed ─────────────────────────────────────────────────────────────
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 60 * scale);
        float sp = g.Speed;
        if (UiHelpers.GradientSlider("Speed##grp", ref sp, 0.01f, 1f, "%.2f")) g.Speed = sp;

        float grpSecs = g.Speed > 0f ? 1f / g.Speed : float.PositiveInfinity;
        ImGui.TextDisabled($"Time to complete: {grpSecs:F1}s");

        // ── Mode ──────────────────────────────────────────────────────────────
        UiHelpers.SectionLabel("Mode:", null);
        ImGui.SameLine();
        int modeInt = (int)g.Mode;
        if (UiHelpers.ButtonGroup("grpm", UiHelpers.MorphModeLabels, ref modeInt))
            g.Mode = (MorphMode)modeInt;

        // ── Easing ────────────────────────────────────────────────────────────
        UiHelpers.SectionLabel("Easing:", null);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160 * scale);
        var gMode = g.Easing;
        var gCurveId = g.EasingCurveId;
        if (UiHelpers.EasingCombo("##grpeas", plugin.Configuration, ref gMode, ref gCurveId))
        {
            g.Easing = gMode;
            g.EasingCurveId = gCurveId;
        }

        // ── Live progress (when this actor is morphing) + remove ──────────────
        if (plugin.IsBrioActorActive(g.ActorIndex))
        {
            float prog = plugin.GetBrioActorProgress(g.ActorIndex);
            UiHelpers.GradientProgressBar(prog, new Vector2(-1, 14 * scale), $"{prog * 100f:F0}%");
        }

        if (UiHelpers.DangerButton("Remove##grp", new Vector2(bw, 0))) removeIdx = index;
        ImGui.Spacing();
    }

    private void DrawPresetsTab()
    {
        using var scroll = ImRaii.Child("##BrioPresetScroll", new Vector2(0, 0), false);
        if (!scroll) return;

        var   config = plugin.Configuration;
        float bw     = 90 * ImGuiHelpers.GlobalScale;

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            $"Save up to {Configuration.PresetSlots} Brio morph configurations for quick access. Each preset remembers " +
            "the actor and MCDF origin it was saved with, so Apply re-targets that actor — " +
            "it only needs to be present in the current GPose scene.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool canSave  = plugin.Configuration.MorphTargetMode == MorphTargetMode.TemplateOverlay
            ? plugin.SelectedBrioTemplateIndex >= 0 && plugin.SelectedBrioTemplateIndex < plugin.SavedTemplates.Count
            : plugin.SelectedBrioDestIndex     >= 0 && plugin.SelectedBrioDestIndex     < plugin.SavedProfiles.Count;
        bool hasActor = plugin.SelectedBrioActorIndex >= 0;
        var  actors   = plugin.GetGPoseActors();

        for (int i = 0; i < Configuration.PresetSlots; i++)
        {
            var  preset    = i < config.BrioPresets.Count ? config.BrioPresets[i] : null;
            bool hasPreset = preset != null;

            // A preset can be applied when it can resolve a target: either its saved actor is
            // present in the scene, or — for legacy presets without a saved actor — an actor is
            // selected in the Single tab.
            bool presetHasTarget = hasPreset && preset!.TargetActorName != null;
            bool targetPresent   = presetHasTarget
                                 && actors.Exists(a => a.Name == preset!.TargetActorName);
            bool canApply        = hasPreset
                                && (targetPresent || (!presetHasTarget && hasActor));

            using (ImRaii.Disabled(!canApply))
            {
                if (UiHelpers.PrimaryButton($"Apply##BrioPreset{i}", new Vector2(bw, 0)))
                    plugin.ApplyBrioMorphPreset(preset!);
            }
            if (hasPreset && !canApply && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(presetHasTarget
                    ? $"Target actor '{preset!.TargetActorName}' is not in the current scene."
                    : "Select an actor in the Single tab first.");

            ImGui.SameLine();
            using (ImRaii.Disabled(!canSave))
            {
                if (ImGui.Button($"Save##BrioPreset{i}", new Vector2(bw, 0)))
                    plugin.SaveBrioPreset(i);
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(!hasPreset))
            {
                if (UiHelpers.DangerButton($"Clear##BrioPreset{i}", new Vector2(bw, 0)))
                    plugin.ClearBrioPreset(i);
            }

            ImGui.SameLine();
            ImGui.TextUnformatted($"{i + 1}.");
            ImGui.SameLine();

            if (hasPreset)
            {
                ImGui.TextUnformatted(preset!.ProfileName);
                ImGui.SameLine();
                ImGui.TextDisabled($"({preset.Speed:F2}/s  {UiHelpers.ModeShort(preset.Mode)}  {UiHelpers.EasingShort(preset.Easing, preset.EasingCurveId, config)}  {UiHelpers.TargetModeShort(preset.TargetMode)})");

                // Second line: the captured target actor and MCDF origin.
                if (preset.TargetActorName != null || preset.OriginMcdfLabel != null)
                {
                    string target = preset.TargetActorName == null
                        ? "any selected actor"
                        : targetPresent
                            ? preset.TargetActorName
                            : $"{preset.TargetActorName} (not in scene)";
                    string mcdf = preset.OriginMcdfLabel ?? "no MCDF";

                    float indent = 3 * bw + 3 * ImGui.GetStyle().ItemSpacing.X;
                    ImGui.Indent(indent);
                    ImGui.TextDisabled($"→ {target}   •   MCDF: {mcdf}");
                    ImGui.Unindent(indent);
                }
            }
            else
            {
                ImGui.TextDisabled("— empty —");
            }
        }
    }

    private void DrawHistory()
    {
        var   config   = plugin.Configuration;
        bool  hasActor = plugin.SelectedBrioActorIndex >= 0;
        float bw       = 90 * ImGuiHelpers.GlobalScale;

        if (config.BrioRecentMorphs.Count == 0) return;

        // Section divider between the morph controls and the history list.
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Recent Morphs##Brio")) return;
        ImGui.Spacing();

        // Applying an entry rewrites BrioRecentMorphs (dedup + reinsert), so defer the call
        // until after the loop to avoid mutating the list we're enumerating.
        MorphPreset? toApply = null;
        foreach (var entry in config.BrioRecentMorphs)
        {
            using (ImRaii.Disabled(!hasActor))
            {
                if (ImGui.Button($"Apply##BrioHist{entry.ProfileId}", new Vector2(bw, 0)))
                    toApply = entry;
            }
            if (!hasActor && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Select an actor in the Single tab first.");
            ImGui.SameLine();
            ImGui.TextUnformatted(entry.ProfileName);
            ImGui.SameLine();
            ImGui.TextDisabled($"({entry.Speed:F2}/s  {UiHelpers.ModeShort(entry.Mode)}  {UiHelpers.EasingShort(entry.Easing, entry.EasingCurveId, config)})");
        }

        if (toApply != null)
            plugin.ApplyBrioMorphPreset(toApply);
    }
}
