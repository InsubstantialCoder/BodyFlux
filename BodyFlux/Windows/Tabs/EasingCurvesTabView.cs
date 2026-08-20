using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using BodyFlux.Morph;

namespace BodyFlux.Windows.Tabs;

/// <summary>
/// "Easing Curve Editor" tab: a card grid of user-authored bezier easing curves plus a modal editor
/// to create/edit them. Selectable from every <see cref="EasingMode"/> combo (Player/Gpose/Sequences/
/// Gauge Link) via <see cref="UiHelpers.EasingCombo"/>.
/// </summary>
public sealed class EasingCurvesTabView
{
    private const string PopupId = "Easing Curve Editor";

    private static readonly EasingMode[] BuiltInModes =
        [EasingMode.Linear, EasingMode.EaseIn, EasingMode.EaseOut, EasingMode.EaseInOut];

    private readonly Plugin plugin;

    // ── Editor popup state ───────────────────────────────────────────────────
    private Guid?   _editingId;
    private string  _editName = "";
    private Vector2 _editP1;
    private Vector2 _editP2;
    private int     _editPresetIndex = -1; // -1 once the user has moved off the picked preset
    private float   _previewElapsed;

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

        if (UiHelpers.PrimaryButton("Add New Easing Curve", new Vector2(200 * scale, 0)))
        {
            BeginNew(config);
            requestOpen = true;
        }

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

                UiHelpers.SectionLabel("Custom Curves", null);
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

                        DrawThumbnail(CubicBezier.Sample(curve.P1, curve.P2, 24), new Vector2(cellWidth, 56 * scale));
                        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + cellWidth);
                        ImGui.TextUnformatted(curve.Name);
                        ImGui.TextDisabled($"({curve.P1.X:F2}, {curve.P1.Y:F2})");
                        ImGui.TextDisabled($"({curve.P2.X:F2}, {curve.P2.Y:F2})");
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
        var preset = EasingCurvePresets.All[0]; // Linear
        _editingId       = null;
        _editName        = $"Curve {config.CustomEasingCurves.Count + 1}";
        _editP1          = preset.P1;
        _editP2          = preset.P2;
        _editPresetIndex = 0;
        _previewElapsed  = 0f;
    }

    private void BeginEdit(EasingCurve curve)
    {
        _editingId       = curve.Id;
        _editName        = curve.Name;
        // Clamp into the canvas's current visible bounds on load — self-heals a curve saved before
        // the handle-drag clamp matched the canvas (points could end up further out than YMin/YMax
        // allow today, which would otherwise reproduce the exact same "handle stuck off-canvas" bug
        // the moment the editor opens). Only touches the in-memory editor copy; Cancel leaves the
        // stored curve as-is.
        _editP1          = ClampToCanvas(curve.P1);
        _editP2          = ClampToCanvas(curve.P2);
        _editPresetIndex = -1;
        _previewElapsed  = 0f;
    }

    private static Vector2 ClampToCanvas(Vector2 p) => new(Math.Clamp(p.X, 0f, 1f), Math.Clamp(p.Y, YMin, YMax));

    // ── Editor popup body ────────────────────────────────────────────────────

    private void DrawEditor(Configuration config, float scale)
    {
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
                    _editP1 = EasingCurvePresets.All[i].P1;
                    _editP2 = EasingCurvePresets.All[i].P2;
                }
                if (sel) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.Spacing();

        bool changed = DrawBezierCanvas(ref _editP1, ref _editP2, 220 * scale);

        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextDisabled("Control points");
        float x1 = _editP1.X, y1 = _editP1.Y, x2 = _editP2.X, y2 = _editP2.Y;
        ImGui.SetNextItemWidth(110 * scale);
        changed |= ImGui.DragFloat("X1", ref x1, 0.002f, 0f, 1f, "%.3f");
        ImGui.SetNextItemWidth(110 * scale);
        changed |= ImGui.DragFloat("Y1", ref y1, 0.005f, YMin, YMax, "%.3f");
        ImGui.SetNextItemWidth(110 * scale);
        changed |= ImGui.DragFloat("X2", ref x2, 0.002f, 0f, 1f, "%.3f");
        ImGui.SetNextItemWidth(110 * scale);
        changed |= ImGui.DragFloat("Y2", ref y2, 0.005f, YMin, YMax, "%.3f");
        // Clamped to the canvas's own visible bounds (not just any float) — a value outside YMin/YMax
        // would place the handle off the drawn canvas, where it's still technically draggable but easy
        // to lose track of and hard to grab again.
        _editP1 = new Vector2(Math.Clamp(x1, 0f, 1f), Math.Clamp(y1, YMin, YMax));
        _editP2 = new Vector2(Math.Clamp(x2, 0f, 1f), Math.Clamp(y2, YMin, YMax));
        ImGui.EndGroup();

        if (changed)
            _editPresetIndex = -1;

        ImGui.Spacing();
        DrawLivePreview(220 * scale, 26 * scale);

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

    private void Save(Configuration config)
    {
        var name = _editName.Trim();
        if (_editingId is { } id)
        {
            int idx = config.CustomEasingCurves.FindIndex(c => c.Id == id);
            if (idx >= 0)
                config.CustomEasingCurves[idx] = config.CustomEasingCurves[idx] with { Name = name, P1 = _editP1, P2 = _editP2 };
        }
        else
        {
            config.CustomEasingCurves.Add(new EasingCurve(Guid.NewGuid(), name, _editP1, _editP2));
        }
        config.Save();
    }

    // ── Canvas + drag handles ────────────────────────────────────────────────

    // Vertical span of the canvas in curve-space: a bit taller than the [0,1] unit square so
    // overshoot handles (y outside 0..1, e.g. the "Back" preset) stay visible and draggable instead
    // of getting clipped at the square's edge.
    private const float YMin = -0.5f;
    private const float YMax = 1.65f; // clears the "Back" preset's P1.Y (1.56) with a little headroom

    /// <summary>Draws the bezier canvas with draggable P1/P2 handles. Returns true if either point moved.</summary>
    private static bool DrawBezierCanvas(ref Vector2 p1, ref Vector2 p2, float size)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin   = ImGui.GetCursorScreenPos();
        var canvas   = new Vector2(size, size);

        Vector2 ToScreen(Vector2 p) => origin + new Vector2(
            p.X * canvas.X,
            (YMax - p.Y) / (YMax - YMin) * canvas.Y);

        Vector2 ToCurveDelta(Vector2 screenDelta) => new(
            screenDelta.X / canvas.X,
            -screenDelta.Y / canvas.Y * (YMax - YMin));

        // Background + unit-square outline.
        drawList.AddRectFilled(origin, origin + canvas, ImGui.GetColorU32(Theme.PanelBg), 4f);
        var squareMin = ToScreen(new Vector2(0f, 1f));
        var squareMax = ToScreen(new Vector2(1f, 0f));
        drawList.AddRect(squareMin, squareMax, ImGui.GetColorU32(Theme.Border));
        drawList.AddRect(origin, origin + canvas, ImGui.GetColorU32(Theme.Border));

        // Guide lines from each fixed endpoint to its handle.
        uint guideCol = ImGui.GetColorU32(Theme.TextMuted);
        drawList.AddLine(ToScreen(Vector2.Zero), ToScreen(p1), guideCol);
        drawList.AddLine(ToScreen(Vector2.One),  ToScreen(p2), guideCol);

        // Curve itself.
        var samples = CubicBezier.Sample(p1, p2, 40);
        uint curveCol = ImGui.GetColorU32(Theme.Accent);
        for (int i = 1; i < samples.Length; i++)
            drawList.AddLine(ToScreen(samples[i - 1]), ToScreen(samples[i]), curveCol, 2f);

        bool changed = false;
        changed |= DragHandle("##p1handle", ref p1, ToScreen, ToCurveDelta, drawList);
        changed |= DragHandle("##p2handle", ref p2, ToScreen, ToCurveDelta, drawList);

        // The handle InvisibleButtons above leave ImGui's layout cursor wherever the last handle
        // happened to be, which would throw off a following SameLine(). Re-stake the canvas's full
        // footprint as one Dummy so layout continues from its bottom edge, same as everything else
        // drawn straight to the draw list in this codebase (see UiHelpers.Card).
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(canvas);

        return changed;
    }

    private static bool DragHandle(string id, ref Vector2 p,
        Func<Vector2, Vector2> toScreen, Func<Vector2, Vector2> toCurveDelta, ImDrawListPtr drawList)
    {
        const float r = 6f;
        var center = toScreen(p);

        ImGui.SetCursorScreenPos(center - new Vector2(r * 2f, r * 2f));
        ImGui.InvisibleButton(id, new Vector2(r * 4f, r * 4f));
        bool hovered = ImGui.IsItemHovered();
        bool active  = ImGui.IsItemActive();

        bool changed = false;
        if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var delta = toCurveDelta(ImGui.GetIO().MouseDelta);
            // Clamped to the canvas's own drawn bounds, not some wider float range — otherwise a fast
            // drag pushes the handle off the visible canvas, where its hit box can end up behind other
            // widgets (the numeric fields, Save/Cancel) and become unreachable to drag back.
            p = new Vector2(Math.Clamp(p.X + delta.X, 0f, 1f), Math.Clamp(p.Y + delta.Y, YMin, YMax));
            changed = true;
        }

        drawList.AddCircleFilled(center, r, ImGui.GetColorU32(active || hovered ? Theme.AccentHover : Vector4.One));
        drawList.AddCircle(center, r, ImGui.GetColorU32(Theme.Accent), 0, 1.5f);

        return changed;
    }

    /// <summary>Small static curve plot for a card in the grid — bezier or built-in, whichever
    /// <paramref name="samples"/> (from <see cref="CubicBezier.Sample"/> or <see cref="EasingHelper.Sample"/>)
    /// came from.</summary>
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
        float eased = CubicBezier.Evaluate(t, _editP1, _editP2);

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
