using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using BodyFlux.Morph;

namespace BodyFlux.Windows.Tabs;

/// <summary>
/// "Easing Curve Editor" tab: a card grid of user-authored easing curves plus a modal editor to
/// create/edit them. Selectable from every <see cref="EasingMode"/> combo (Player/Gpose/Sequences/
/// Gauge Link) via <see cref="UiHelpers.EasingCombo"/>.
/// <para>
/// A curve is edited as a list of <see cref="CurveKey"/> anchors the curve passes through, each with
/// its own tangent handles. The two-control-point form the editor started out with is just the
/// two-anchor case of it, and an old saved curve arrives here through
/// <see cref="EasingCurve.Resolve"/> looking exactly like one. Every edit goes through
/// <see cref="CurveKeys"/>, which owns all the clamping; this file only turns mouse positions into
/// requests and draws the result.
/// </para>
/// </summary>
public sealed class EasingCurvesTabView
{
    private const string PopupId       = "Easing Curve Editor";
    private const string AnchorMenuId  = "##EasingAnchorMenu";

    private static readonly EasingMode[] BuiltInModes =
        [EasingMode.Linear, EasingMode.EaseIn, EasingMode.EaseOut, EasingMode.EaseInOut];

    private readonly Plugin plugin;

    // ── Editor popup state ───────────────────────────────────────────────────
    private Guid?          _editingId;
    private string         _editName        = "";
    private List<CurveKey> _editKeys        = [];
    private int            _editSelected;            // index into _editKeys; the only anchor whose handles are drawn
    private int            _editPresetIndex = -1;    // -1 once the user has moved off the picked preset
    private float          _previewElapsed;

    public EasingCurvesTabView(Plugin plugin) => this.plugin = plugin;

    // Fixed card width for the grid — how many columns fit is derived from this and the available
    // width each frame, rather than a fixed column count, so the grid re-flows with the window size.
    private const float CardWidth = 170f;

    public void Draw()
    {
        var  config = plugin.Configuration;
        float scale = ImGuiHelpers.GlobalScale;
        bool  requestOpen = false;
        int   deleteIdx   = -1;

        ImGui.Spacing();

        using (var scroll = ImRaii.Child("##EasingCurveScroll", new Vector2(0, 0), false))
        {
            if (scroll)
            {
                UiHelpers.SectionLabel("Built-in Curves",
                    "The 4 curves BodyFlux ships with. Fixed shapes, shown here only as a visual " +
                    "reference — not editable.");
                ImGui.Spacing();
                DrawCardGrid(BuiltInModes.Length, scale, "##EasingBuiltInGrid", (i, cellWidth) =>
                {
                    var mode = BuiltInModes[i];
                    DrawThumbnail(EasingHelper.Sample(mode, 24), new Vector2(cellWidth, 56 * scale));
                    ImGui.TextUnformatted(UiHelpers.EasingNames[(int)mode]);
                });

                ImGui.Spacing();
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                ImGui.Spacing();

                UiHelpers.SectionLabel("Custom Curves",
                    "Create your own easing curves by dragging points on a graph, up to 8 of them, " +
                    "so a curve can pause, overshoot or bounce partway through instead of only " +
                    "bending once. Save one here, then pick it from any Easing dropdown in the " +
                    "plugin (Player, Gpose, Sync, Sequences, Gauge Link) instead of the 4 built-in shapes.");
                ImGui.Spacing();

                if (UiHelpers.PrimaryButton("Add New Easing Curve", new Vector2(200 * scale, 0)))
                {
                    BeginNew(config);
                    requestOpen = true;
                }
                ImGui.Spacing();

                if (config.CustomEasingCurves.Count == 0)
                {
                    ImGui.TextDisabled("No custom curves yet — press Add New Easing Curve.");
                }
                else
                {
                    DrawCardGrid(config.CustomEasingCurves.Count, scale, "##EasingCustomGrid", (i, cellWidth) =>
                    {
                        var curve = config.CustomEasingCurves[i];

                        // Resolve() so a curve stored in either shape plots identically: an old
                        // P1/P2 curve is projected onto its equivalent two anchors on the way in.
                        DrawThumbnail(CubicBezier.SampleCurve(curve.Resolve(), 24), new Vector2(cellWidth, 56 * scale));
                        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + cellWidth);
                        ImGui.TextUnformatted(curve.Name);
                        // The two control-point coordinates only describe a two-anchor curve; for a
                        // multi-point one they are the downgrade approximation EasingCurve keeps, not
                        // the shape, so printing them would be actively misleading. The card is
                        // ~170px wide, so the alternative has to stay to one short line.
                        if (curve.PointCount == 2)
                        {
                            ImGui.TextDisabled($"({curve.P1.X:F2}, {curve.P1.Y:F2})");
                            ImGui.TextDisabled($"({curve.P2.X:F2}, {curve.P2.Y:F2})");
                        }
                        else
                        {
                            ImGui.TextDisabled($"{curve.PointCount} points");
                            ImGui.TextDisabled(" "); // keeps every card the same height, points or not
                        }
                        ImGui.PopTextWrapPos();

                        float btnWidth = (cellWidth - ImGui.GetStyle().ItemSpacing.X) / 2f;
                        if (ImGui.Button("Edit", new Vector2(btnWidth, 0)))
                        {
                            BeginEdit(curve);
                            requestOpen = true;
                        }
                        ImGui.SameLine();
                        if (UiHelpers.DangerButton("Delete", new Vector2(btnWidth, 0)))
                            deleteIdx = i;
                    });
                }
            }
        }

        if (deleteIdx >= 0)
        {
            config.CustomEasingCurves.RemoveAt(deleteIdx);
            config.Save();
        }

        // OpenPopup/BeginPopupModal must be called at the same ID-stack depth, so the open request
        // is deferred here — outside every PushID(i) scope from the loop above — rather than fired
        // from inside it, which would hash the popup ID against the wrong stack and silently fail
        // to open it.
        if (requestOpen)
            ImGui.OpenPopup(PopupId);

        if (ImGui.BeginPopupModal(PopupId, ImGuiWindowFlags.AlwaysAutoResize))
        {
            DrawEditor(config, scale);
            ImGui.EndPopup();
        }
    }

    /// <summary>
    /// Lays out <paramref name="itemCount"/> cards into a responsive grid via ImGui's table API —
    /// columns stretch evenly to fill the available width, and the table owns all row-wrapping itself.
    /// (An earlier hand-rolled version tracked columns with SameLine()+modulo and manual cursor
    /// restaking; once this region needed to scroll it reliably produced misaligned rows and blank
    /// "ghost" card backgrounds — tables are what ImGui provides specifically for this.)
    /// <paramref name="drawContent"/> receives the item index and that cell's available width; each
    /// cell gets a <see cref="CardBox"/> background/border drawn around whatever it draws.
    /// </summary>
    private static void DrawCardGrid(int itemCount, float scale, string tableId, Action<int, float> drawContent)
    {
        if (itemCount == 0) return;

        float cardWidth = CardWidth * scale;
        float avail     = ImGui.GetContentRegionAvail().X;
        int   columns   = Math.Max(1, Math.Min(itemCount, (int)(avail / cardWidth)));

        if (!ImGui.BeginTable(tableId, columns, ImGuiTableFlags.SizingStretchSame))
            return;

        for (int i = 0; i < itemCount; i++)
        {
            ImGui.TableNextColumn();
            ImGui.PushID(i);
            CardBox(ImGui.GetContentRegionAvail().X, innerWidth => drawContent(i, innerWidth));
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// Rounded, bordered card of a fixed <paramref name="width"/> — same channel-split background
    /// technique as <see cref="UiHelpers.Card"/>, but parameterized on width instead of always
    /// filling the row. <paramref name="content"/> receives the padded inner width to size its own
    /// children with.
    /// </summary>
    private static void CardBox(float width, Action<float> content)
    {
        const float pad = 10f;

        var   drawList   = ImGui.GetWindowDrawList();
        float rounding   = ImGui.GetStyle().FrameRounding;
        float innerWidth = width - pad * 2f;
        var   outerMin   = ImGui.GetCursorScreenPos();

        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.Dummy(new Vector2(1, pad));
        ImGui.Indent(pad);
        content(innerWidth);
        ImGui.Unindent(pad);
        ImGui.Dummy(new Vector2(1, pad));

        var outerMax = new Vector2(outerMin.X + width, ImGui.GetCursorScreenPos().Y);

        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(outerMin, outerMax, ImGui.GetColorU32(Theme.PanelBg), rounding);
        drawList.AddRect(outerMin, outerMax, ImGui.GetColorU32(Theme.Border), rounding);
        drawList.ChannelsMerge();
    }

    // ── Popup lifecycle ──────────────────────────────────────────────────────

    private void BeginNew(Configuration config)
    {
        _editingId       = null;
        _editName        = $"Curve {config.CustomEasingCurves.Count + 1}";
        // Sanitize copies as well as validates, which is the point here: the preset table is static
        // and shared, so the editor must never end up holding a reference into it and re-shaping a
        // preset for the rest of the session.
        _editKeys        = CurveKeys.Sanitize(EasingCurvePresets.All[0].Keys); // Linear
        _editSelected    = 0;
        _editPresetIndex = 0;
        _previewElapsed  = 0f;
    }

    private void BeginEdit(EasingCurve curve)
    {
        _editingId       = curve.Id;
        _editName        = curve.Name;
        // Sanitize on load self-heals a curve that doesn't meet today's invariants, one saved before
        // the drag clamp matched the canvas, or one hand-edited in the config file. Without it a
        // point can sit off the drawn canvas, where it's still technically draggable but impossible
        // to find and grab again. Only touches the in-memory editor copy; Cancel leaves the stored
        // curve exactly as it was.
        _editKeys        = CurveKeys.Sanitize(curve.Resolve());
        _editSelected    = 0;
        _editPresetIndex = -1;
        _previewElapsed  = 0f;
    }

    // ── Editor popup body ────────────────────────────────────────────────────

    private void DrawEditor(Configuration config, float scale)
    {
        ClampSelection();

        ImGui.SetNextItemWidth(260 * scale);
        ImGui.InputText("Name", ref _editName, 64);

        ImGui.SetNextItemWidth(260 * scale);
        string presetLabel = _editPresetIndex >= 0 ? EasingCurvePresets.All[_editPresetIndex].Name : "Custom";
        if (ImGui.BeginCombo("Start from", presetLabel))
        {
            for (int i = 0; i < EasingCurvePresets.All.Length; i++)
            {
                bool sel = i == _editPresetIndex;
                if (ImGui.Selectable(EasingCurvePresets.All[i].Name, sel))
                {
                    _editPresetIndex = i;
                    _editKeys        = CurveKeys.Sanitize(EasingCurvePresets.All[i].Keys);
                    _editSelected    = 0;
                }
                if (sel) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.Spacing();

        // A bit bigger than before (was 220) to compensate for the wider YMin/YMax range now mapped
        // onto it — otherwise the [0,1] unit square would shrink noticeably as a fraction of the box.
        bool openAnchorMenu = false;
        bool changed        = DrawCurveCanvas(260 * scale, ref openAnchorMenu);

        ImGui.SameLine();
        ImGui.BeginGroup();
        changed |= DrawPointPanel(scale);
        ImGui.EndGroup();

        // Same ID-stack rule as the editor popup itself: the request is raised inside the canvas and
        // acted on here, at the depth BeginPopup is called from.
        if (openAnchorMenu)
            ImGui.OpenPopup(AnchorMenuId);
        changed |= DrawAnchorMenu();

        if (changed)
            _editPresetIndex = -1;

        ImGui.Spacing();
        DrawLivePreview(260 * scale, 26 * scale); // matches the canvas width above it

        ImGui.Spacing();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_editName)))
        {
            if (UiHelpers.PrimaryButton("Save", new Vector2(100 * scale, 0)))
            {
                Save(config);
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100 * scale, 0)))
            ImGui.CloseCurrentPopup();
    }

    /// <summary>
    /// Right-hand column: how many points the curve has, the numeric fields for the selected one, and
    /// the button equivalents of the canvas gestures. Deliberately shows the selected point only:
    /// with up to <see cref="CurveKeys.MaxKeys"/> anchors a field per point would make the popup grow
    /// and shrink as points come and go, which under AlwaysAutoResize reads as the window glitching.
    /// Returns true if anything here re-shaped the curve.
    /// </summary>
    private bool DrawPointPanel(float scale)
    {
        ClampSelection();

        int   index   = _editSelected;
        var   key     = _editKeys[index];
        int   count   = _editKeys.Count;
        bool  pinned  = index == 0 || index == count - 1;
        float fieldW  = 110 * scale;
        bool  changed = false;

        ImGui.TextDisabled($"Points: {count} / {CurveKeys.MaxKeys}");
        ImGui.TextDisabled(pinned ? $"Point {index + 1} (fixed)" : $"Point {index + 1}");
        ImGui.Spacing();

        // The first and last anchors are pinned at (0,0) and (1,1) by CurveKeys' invariants, since
        // a morph always starts where it started and ends where it's going. Their fields are shown disabled
        // rather than hidden, again so the panel keeps one fixed height.
        using (ImRaii.Disabled(pinned))
        {
            float px = key.Pos.X, py = key.Pos.Y;
            // Interior anchors keep MinGap clear of both neighbours; feeding those same bounds to the
            // slider keeps the displayed number honest instead of letting it run past what
            // MoveAnchor will accept and then snap back.
            float minX = pinned ? 0f : _editKeys[index - 1].Pos.X + CurveKeys.MinGap;
            float maxX = pinned ? 1f : _editKeys[index + 1].Pos.X - CurveKeys.MinGap;

            ImGui.SetNextItemWidth(fieldW);
            bool moved = ImGui.DragFloat("X", ref px, 0.002f, minX, MathF.Max(minX, maxX), "%.3f");
            ImGui.SetNextItemWidth(fieldW);
            moved |= ImGui.DragFloat("Y", ref py, 0.005f, CurveKeys.YMin, CurveKeys.YMax, "%.3f");

            if (moved)
            {
                _editKeys = CurveKeys.MoveAnchor(_editKeys, index, new Vector2(px, py));
                changed   = true;
            }
        }

        ImGui.Spacing();
        changed |= DrawHandleFields("In",  index, outgoing: false, fieldW);
        changed |= DrawHandleFields("Out", index, outgoing: true,  fieldW);

        ImGui.Spacing();
        using (ImRaii.Disabled(!CurveKeys.CanInsert(_editKeys)))
        {
            if (ImGui.Button("Add Point", new Vector2(fieldW, 0)))
                changed |= InsertAtWidestSegment();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Splits the widest segment in half. Up to {CurveKeys.MaxKeys} points.");

        using (ImRaii.Disabled(pinned))
        {
            if (ImGui.Button("Delete Point", new Vector2(fieldW, 0)))
                changed |= RemoveSelected();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Removes the selected point. The first and last points can't be removed.");

        ImGui.Spacing();
        // ImGui gives BeginPopupModal's own title bar no public API to attach a widget to (it's
        // drawn as window chrome, not an item we called), so this can't literally sit in the title
        // bar, so it is placed in the leftover space under the point fields instead.
        UiHelpers.DrawHelpIcon(
            "Click a point to select it. Only the selected point shows its two handles, joined to " +
            "it by thin lines. Drag a point to move it, drag a handle to bend the segment on that " +
            "side. Double-click empty space on the graph to add a point (the curve keeps its exact " +
            "shape), right-click a point to delete it. X is time and stays inside the point's own " +
            "stretch of it; Y can go beyond 0–1 for overshoot effects like Back.\n\n" +
            "One caveat for Sync: a morph is sent to other players about 20 times a second and their " +
            "client smooths between those samples, so anything narrower than roughly 50ms of the run " +
            "(a very short segment, or any segment when Morph Time is very short) will look softened " +
            "on their screen even though it's exact on yours.");

        return changed;
    }

    /// <summary>
    /// The x/y offset fields for one of the selected anchor's tangent handles. Offsets, not absolute
    /// positions, so the numbers keep meaning the same thing as the anchor moves. That's how
    /// <see cref="CurveKey"/> stores them.
    /// </summary>
    private bool DrawHandleFields(string label, int index, bool outgoing, float width)
    {
        var key    = _editKeys[index];
        var offset = outgoing ? key.Out : key.In;

        // A handle only exists on a side that has a segment: nothing arrives at the first anchor,
        // nothing leaves the last. MoveHandle ignores those, so the fields are disabled to say so
        // rather than silently swallowing the edit.
        bool  unused = outgoing ? index == _editKeys.Count - 1 : index == 0;
        float span   = unused    ? 0f
                     : outgoing  ? _editKeys[index + 1].Pos.X - key.Pos.X
                                 : key.Pos.X - _editKeys[index - 1].Pos.X;

        using var disabled = ImRaii.Disabled(unused);

        float ox = offset.X, oy = offset.Y;
        ImGui.SetNextItemWidth(width);
        bool moved = ImGui.DragFloat($"{label} X", ref ox, 0.002f,
            outgoing ? 0f : -span, outgoing ? span : 0f, "%.3f");
        ImGui.SetNextItemWidth(width);
        // The y bounds are the canvas bounds expressed as an offset from this anchor, so a handle
        // can reach the whole visible height and no further, wherever its anchor happens to sit.
        moved |= ImGui.DragFloat($"{label} Y", ref oy, 0.005f,
            CurveKeys.YMin - key.Pos.Y, CurveKeys.YMax - key.Pos.Y, "%.3f");

        if (!moved) return false;

        _editKeys = CurveKeys.MoveHandle(_editKeys, index, outgoing, new Vector2(ox, oy));
        return true;
    }

    /// <summary>
    /// Right-click menu for the anchor under the cursor. Delete is disabled rather than absent on the
    /// two pinned endpoints: <see cref="CurveKeys.Remove"/> refuses them by returning null, and a
    /// greyed item explains that where an error toast after the fact would not.
    /// </summary>
    private bool DrawAnchorMenu()
    {
        if (!ImGui.BeginPopup(AnchorMenuId)) return false;

        ClampSelection();
        bool changed = false;
        bool pinned  = _editSelected == 0 || _editSelected == _editKeys.Count - 1;

        ImGui.TextDisabled($"Point {_editSelected + 1} of {_editKeys.Count}");
        ImGui.Separator();
        using (ImRaii.Disabled(pinned))
        {
            if (ImGui.MenuItem("Delete"))
            {
                changed = RemoveSelected();
                ImGui.CloseCurrentPopup();
            }
        }
        if (pinned)
            ImGui.TextDisabled("First and last points are fixed.");

        ImGui.EndPopup();
        return changed;
    }

    private void Save(Configuration config)
    {
        var name = _editName.Trim();
        // FromKeys sanitizes and fills P1/P2 with the two-point reading of the shape for the
        // downgrade path, so neither is written by hand here.
        if (_editingId is { } id)
        {
            int idx = config.CustomEasingCurves.FindIndex(c => c.Id == id);
            if (idx >= 0)
                config.CustomEasingCurves[idx] = EasingCurve.FromKeys(id, name, _editKeys);
        }
        else
        {
            config.CustomEasingCurves.Add(EasingCurve.FromKeys(Guid.NewGuid(), name, _editKeys));
        }
        config.Save();
    }

    // ── Key-list edits shared by the canvas and the panel ─────────────────────

    /// <summary>
    /// Keeps <see cref="_editSelected"/> pointing at a real anchor, and the key list at something
    /// drawable. Both can go stale between frames, a point removed from the menu shortens the list
    /// under the selection, and every draw path starts by calling this rather than range-checking
    /// at each use.
    /// </summary>
    private void ClampSelection()
    {
        if (_editKeys.Count < 2)
            _editKeys = CurveKeys.Sanitize(null); // linear fallback, never an unusable editor
        _editSelected = Math.Clamp(_editSelected, 0, _editKeys.Count - 1);
    }

    /// <summary>
    /// Adds an anchor in the middle of the widest segment, the one place guaranteed to clear
    /// <see cref="CurveKeys.MinGap"/> on both sides if anywhere does, so the button never appears to
    /// do nothing while the curve still has room.
    /// </summary>
    private bool InsertAtWidestSegment()
    {
        int   at   = 0;
        float best = -1f;
        for (int i = 0; i < _editKeys.Count - 1; i++)
        {
            float span = _editKeys[i + 1].Pos.X - _editKeys[i].Pos.X;
            if (span <= best) continue;
            best = span;
            at   = i;
        }

        return InsertAt((_editKeys[at].Pos.X + _editKeys[at + 1].Pos.X) / 2f);
    }

    /// <summary>
    /// Inserts an anchor at time <paramref name="x"/> and selects it. Null from
    /// <see cref="CurveKeys.Insert"/> means there was no room (the cap, or too close to a neighbour);
    /// that's a silent no-op on purpose, both callers are gestures that cost nothing to repeat
    /// somewhere else.
    /// </summary>
    private bool InsertAt(float x)
    {
        var inserted = CurveKeys.Insert(_editKeys, x);
        if (inserted is null) return false;

        // Insert splits with de Casteljau, so the new anchor lands exactly on the curve that was
        // already drawn, nothing moves. Finding it back by x is safe for the same reason: it sits
        // at the requested time, and MinGap keeps every other anchor well clear of it.
        int at = 0;
        for (int i = 1; i < inserted.Count; i++)
            if (MathF.Abs(inserted[i].Pos.X - x) < MathF.Abs(inserted[at].Pos.X - x)) at = i;

        _editKeys     = inserted;
        _editSelected = at;
        return true;
    }

    private bool RemoveSelected()
    {
        var reduced = CurveKeys.Remove(_editKeys, _editSelected);
        if (reduced is null) return false; // endpoints and two-point curves refuse, by design

        _editKeys     = reduced;
        _editSelected = Math.Clamp(_editSelected, 0, _editKeys.Count - 1);
        return true;
    }

    // ── Canvas + drag handles ────────────────────────────────────────────────

    // The canvas maps CurveKeys.YMin..YMax vertically, not the [0,1] unit square, so overshoot points
    // (y outside 0..1, e.g. the "Back" preset) have real room to be dragged into. Value range and
    // drawn box read the same two constants on purpose: the old bug here wasn't "a point went past
    // the unit square", it was letting a drag go past *this box* entirely, out into whatever widget
    // was drawn next (the numeric fields, Save/Cancel) where it stole the click and could never be
    // grabbed back. Those constants now live in CurveKeys, which is also what clamps against them,
    // one source of truth for the bounds and the clamping both.

    /// <summary>
    /// Draws the curve, its anchors and the selected anchor's handles, and runs every mouse gesture
    /// over them. Returns true if the curve changed this frame; sets <paramref name="openAnchorMenu"/>
    /// when a right-click asked for the per-anchor menu.
    /// </summary>
    private bool DrawCurveCanvas(float size, ref bool openAnchorMenu)
    {
        ClampSelection();

        var drawList = ImGui.GetWindowDrawList();
        var origin   = ImGui.GetCursorScreenPos();
        var canvas   = new Vector2(size, size);

        Vector2 ToScreen(Vector2 p) => origin + new Vector2(
            p.X * canvas.X,
            (CurveKeys.YMax - p.Y) / (CurveKeys.YMax - CurveKeys.YMin) * canvas.Y);

        Vector2 ToCurveDelta(Vector2 screenDelta) => new(
            screenDelta.X / canvas.X,
            -screenDelta.Y / canvas.Y * (CurveKeys.YMax - CurveKeys.YMin));

        // Background + unit-square outline.
        drawList.AddRectFilled(origin, origin + canvas, ImGui.GetColorU32(Theme.PanelBg), 4f);
        var squareMin = ToScreen(new Vector2(0f, 1f));
        var squareMax = ToScreen(new Vector2(1f, 0f));
        drawList.AddRect(squareMin, squareMax, ImGui.GetColorU32(Theme.Border));
        drawList.AddRect(origin, origin + canvas, ImGui.GetColorU32(Theme.Border));

        // Curve itself. Sampled finer than the old two-point canvas needed: SampleCurve steps evenly
        // across the whole 0..1, so a short segment only gets its share of the samples, and a curve
        // that does something abrupt inside one would visibly show its corners cut at 40.
        var  samples  = CubicBezier.SampleCurve(_editKeys, 120);
        uint curveCol = ImGui.GetColorU32(Theme.Accent);
        for (int i = 1; i < samples.Length; i++)
            drawList.AddLine(ToScreen(samples[i - 1]), ToScreen(samples[i]), curveCol, 2f);

        bool changed = false;

        // The full-canvas hit target (double-click to add a point) goes in FIRST and then steps
        // aside: ImGui hands hovering to the first item that claims it, so everything submitted after
        // this would be dead to the mouse unless the canvas explicitly allows itself to be
        // overlapped. Submission order is therefore priority order, lowest first:
        // canvas → handles → anchors.
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##EasingCanvas", canvas);
        ImGui.SetItemAllowOverlap();
        bool  canvasDoubleClick = ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        float clickX            = (ImGui.GetIO().MousePos.X - origin.X) / canvas.X;

        // Handles are drawn for the selected anchor only. With up to 8 anchors the full set is 14
        // handles at once, which is unreadable, and a lone pair is never ambiguous about which anchor
        // it steers.
        bool pointGrabbed = false;
        if (_editSelected > 0)
            changed |= DragHandle(_editSelected, outgoing: false, ToScreen, ToCurveDelta, drawList, ref pointGrabbed);
        if (_editSelected < _editKeys.Count - 1)
            changed |= DragHandle(_editSelected, outgoing: true,  ToScreen, ToCurveDelta, drawList, ref pointGrabbed);

        // Anchors last, so they win the hit test over both the canvas and a handle parked on top of
        // one. Iterating over a snapshot of the count because a drag inside the loop replaces
        // _editKeys, and the list is never mutated in place, each helper returns a new one.
        for (int i = 0; i < _editKeys.Count; i++)
            changed |= DragAnchor(i, ToScreen, ToCurveDelta, drawList, ref pointGrabbed, ref openAnchorMenu);

        // Only now is it known whether the double-click landed on a point: the points are submitted
        // after the canvas, so their hover state doesn't exist yet at the point the canvas reports
        // its own. Double-clicking a point is a select-and-drag misfire, not a request for a new one.
        if (canvasDoubleClick && !pointGrabbed)
            changed |= InsertAt(clickX);

        // The InvisibleButtons above leave ImGui's layout cursor wherever the last one happened to
        // be, which would throw off a following SameLine(). Re-stake the canvas's full footprint as
        // one Dummy so layout continues from its bottom edge, same as everything else drawn straight
        // to the draw list in this codebase (see UiHelpers.Card).
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(canvas);

        return changed;
    }

    /// <summary>
    /// One anchor: a square marker (so it never reads as one of the round handles), click to select,
    /// drag to move, right-click for its menu.
    /// </summary>
    private bool DragAnchor(int index,
        Func<Vector2, Vector2> toScreen, Func<Vector2, Vector2> toCurveDelta, ImDrawListPtr drawList,
        ref bool pointGrabbed, ref bool openAnchorMenu)
    {
        const float r   = 5f;
        const float hit = 10f; // wider than a handle's box, so an anchor stays grabbable around the
                               // edges even with a zero-length handle sitting exactly on top of it

        var  key      = _editKeys[index];
        var  center   = toScreen(key.Pos);
        bool selected = index == _editSelected;
        bool pinned   = index == 0 || index == _editKeys.Count - 1;

        ImGui.SetCursorScreenPos(center - new Vector2(hit, hit));
        ImGui.InvisibleButton($"##anchor{index}", new Vector2(hit * 2f, hit * 2f));
        bool hovered = ImGui.IsItemHovered();
        bool active  = ImGui.IsItemActive();
        pointGrabbed |= hovered || active;

        if (active || ImGui.IsItemClicked())
            _editSelected = index;

        bool changed = false;
        if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            // All clamping lives in MoveAnchor, no second and looser clamp here. Two clamps that can
            // disagree is exactly how a point ended up reachable off-canvas before.
            _editKeys = CurveKeys.MoveAnchor(_editKeys, index, key.Pos + toCurveDelta(ImGui.GetIO().MouseDelta));
            changed   = true;
        }

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _editSelected  = index;
            openAnchorMenu = true;
        }

        var half = new Vector2(r, r);
        var fill = active || hovered ? Theme.AccentHover
                 : selected          ? Theme.Accent
                                     : Vector4.One;
        drawList.AddRectFilled(center - half, center + half, ImGui.GetColorU32(fill), 1f);
        drawList.AddRect(center - half, center + half,
            ImGui.GetColorU32(pinned ? Theme.TextMuted : Theme.Accent), 1f);

        return changed;
    }

    /// <summary>
    /// One tangent handle of the selected anchor, drawn as a circle on a thin leash back to its
    /// anchor so a handle far out still reads as belonging to it rather than as a stray point.
    /// </summary>
    private bool DragHandle(int index, bool outgoing,
        Func<Vector2, Vector2> toScreen, Func<Vector2, Vector2> toCurveDelta, ImDrawListPtr drawList,
        ref bool pointGrabbed)
    {
        const float r   = 5f;
        const float hit = 7f; // deliberately smaller than an anchor's box, see DragAnchor

        var key    = _editKeys[index];
        var offset = outgoing ? key.Out : key.In;
        var center = toScreen(key.Pos + offset);

        drawList.AddLine(toScreen(key.Pos), center, ImGui.GetColorU32(Theme.TextMuted));

        ImGui.SetCursorScreenPos(center - new Vector2(hit, hit));
        ImGui.InvisibleButton(outgoing ? $"##handleOut{index}" : $"##handleIn{index}", new Vector2(hit * 2f, hit * 2f));
        bool hovered = ImGui.IsItemHovered();
        bool active  = ImGui.IsItemActive();
        pointGrabbed |= hovered || active;

        bool changed = false;
        if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            // MoveHandle owns the clamping: an outgoing handle reaches forward at most to the next
            // anchor, an incoming one back at most to the previous, and y stays on the canvas.
            _editKeys = CurveKeys.MoveHandle(_editKeys, index, outgoing, offset + toCurveDelta(ImGui.GetIO().MouseDelta));
            changed   = true;
        }

        drawList.AddCircleFilled(center, r, ImGui.GetColorU32(active || hovered ? Theme.AccentHover : Vector4.One));
        drawList.AddCircle(center, r, ImGui.GetColorU32(Theme.Accent), 0, 1.5f);

        return changed;
    }

    /// <summary>Small static curve plot for a card in the grid: multi-point, bezier or built-in,
    /// whichever <paramref name="samples"/> (from <see cref="CubicBezier.SampleCurve"/> or
    /// <see cref="EasingHelper.Sample"/>) came from.</summary>
    private static void DrawThumbnail(Vector2[] samples, Vector2 size)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin   = ImGui.GetCursorScreenPos();

        drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(Theme.PanelBg), 4f);
        drawList.AddRect(origin, origin + size, ImGui.GetColorU32(Theme.Border), 4f);

        uint col = ImGui.GetColorU32(Theme.Accent);
        // Pad the plotted range past 0..1 so an overshoot curve (e.g. the "Back" preset) doesn't
        // clip against the thumbnail's frame.
        const float pad = 0.35f;
        Vector2 ToLocal(Vector2 s) => origin + new Vector2(
            s.X * size.X,
            (1f + pad - s.Y) / (1f + pad * 2f) * size.Y);
        for (int i = 1; i < samples.Length; i++)
            drawList.AddLine(ToLocal(samples[i - 1]), ToLocal(samples[i]), col, 1.5f);

        ImGui.Dummy(size);
    }

    /// <summary>Animated dot sweeping the eased value over a looping ~1.2s cycle, for a feel of the curve's motion.</summary>
    private void DrawLivePreview(float width, float height)
    {
        const float period = 1.2f;
        _previewElapsed += ImGui.GetIO().DeltaTime;
        float t = (_previewElapsed % period) / period;
        // EvaluateCurve, not Evaluate: the whole point of a multi-point curve is the plateaus and
        // overshoots a single cubic can't express, and those are exactly what this is here to show.
        float eased = CubicBezier.EvaluateCurve(t, _editKeys);

        var drawList = ImGui.GetWindowDrawList();
        var origin   = ImGui.GetCursorScreenPos();
        var size     = new Vector2(width, height);

        drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(Theme.TrackBg), height / 2f);
        drawList.AddRect(origin, origin + size, ImGui.GetColorU32(Theme.Border), height / 2f);

        float dotX = origin.X + Math.Clamp(eased, 0f, 1f) * size.X;
        var   center = new Vector2(dotX, origin.Y + size.Y / 2f);
        drawList.AddCircleFilled(center, height / 2f - 2f, ImGui.GetColorU32(Theme.Accent));

        ImGui.Dummy(size);
    }
}
