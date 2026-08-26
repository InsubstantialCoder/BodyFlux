using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using BodyFlux.Morph;
using BodyFlux.Windows;

namespace BodyFlux.Windows.Tabs;

/// <summary>
/// Small label/formatting helpers shared by the tab views (Player and Brio history,
/// presets, and easing combos). Pure presentation — no state.
/// </summary>
internal static class UiHelpers
{
    // Shared between Player and Brio's Single tabs, same choices, same ButtonGroup rows.
    public static readonly string[] MorphTargetLabels = ["Full Profile", "Template Overlay"];
    public static readonly string[] MorphModeLabels    = ["Simple", "Loop (x1)", "Loop (∞)"];
    /// <summary>Display names indexed by <c>(int)</c><see cref="EasingMode"/> — used by <see cref="EasingCombo"/>.</summary>
    public static readonly string[] EasingNames = ["Linear", "Ease In", "Ease Out", "Ease In-Out"];

    public static string ModeShort(MorphMode m) => m switch
    {
        MorphMode.LoopSingle   => "Loop (x1)",
        MorphMode.LoopInfinite => "Loop (∞)",
        _                      => "Simple"
    };

    public static string EasingShort(EasingMode e) => e switch
    {
        EasingMode.EaseIn    => "Ease In",
        EasingMode.EaseOut   => "Ease Out",
        EasingMode.EaseInOut => "In-Out",
        _                    => "Linear"
    };

    /// <summary>
    /// Overload of <see cref="EasingShort(EasingMode)"/> for a preset/history row that may carry a
    /// custom curve reference: returns the curve's name when <paramref name="curveId"/> resolves,
    /// <c>"(deleted curve)"</c> if it's set but no longer found (mirrors <see cref="PresetOriginLabel"/>'s
    /// "(deleted profile)" fallback), else falls back to the plain built-in short label.
    /// </summary>
    public static string EasingShort(EasingMode mode, Guid? curveId, Configuration config) => curveId switch
    {
        null => EasingShort(mode),
        { } id => config.CustomEasingCurves.Find(c => c.Id == id) is { } curve ? $"Custom: {curve.Name}" : "(deleted curve)",
    };

    /// <summary>
    /// Combo listing the 4 built-in <see cref="EasingMode"/> values plus every saved custom curve
    /// (a separator between the two groups, omitted entirely when there are no custom curves yet —
    /// so it looks identical to a plain built-in combo until the user has saved one). Picking a
    /// built-in clears <paramref name="curveId"/>; picking a custom curve leaves
    /// <paramref name="mode"/> untouched (it's just the "last built-in" the picker remembers) and
    /// sets <paramref name="curveId"/>.
    /// </summary>
    public static bool EasingCombo(string comboId, Configuration config, ref EasingMode mode, ref Guid? curveId)
    {
        string preview = curveId is { } cid
            ? (config.CustomEasingCurves.Find(c => c.Id == cid) is { } found ? $"Custom: {found.Name}" : "(deleted curve)")
            : EasingNames[(int)mode];

        bool changed = false;
        if (!ImGui.BeginCombo(comboId, preview)) return false;

        for (int i = 0; i < EasingNames.Length; i++)
        {
            bool sel = curveId == null && (int)mode == i;
            if (ImGui.Selectable(EasingNames[i], sel))
            {
                mode = (EasingMode)i;
                curveId = null;
                changed = true;
            }
            if (sel) ImGui.SetItemDefaultFocus();
        }

        if (config.CustomEasingCurves.Count > 0)
        {
            ImGui.Separator();
            foreach (var curve in config.CustomEasingCurves)
            {
                bool sel = curveId == curve.Id;
                if (ImGui.Selectable($"Custom: {curve.Name}", sel))
                {
                    curveId = curve.Id;
                    changed = true;
                }
                if (sel) ImGui.SetItemDefaultFocus();
            }
        }

        ImGui.EndCombo();
        return changed;
    }

    public static string TargetModeShort(MorphTargetMode m) => m switch
    {
        MorphTargetMode.TemplateOverlay => "Overlay",
        _                               => "Full"
    };

    /// <summary>
    /// Origin half of a preset/history row's "From → To" label. A preset with no pinned origin
    /// (<see cref="MorphPreset.OriginProfileId"/> null) started from whatever was live on the
    /// character at Apply time, so that's shown instead of a profile name.
    /// </summary>
    public static string PresetOriginLabel(MorphPreset preset) =>
        preset.OriginProfileId is null ? "live profile" : preset.OriginProfileName ?? "(deleted profile)";

    /// <summary>
    /// Filterable, searchable combo box over a flat list of names — shared by every destination
    /// picker (Player/Brio Single, Group rows, Sequence add-step), which each switch between
    /// listing Profiles or Templates depending on the current <see cref="MorphTargetMode"/>.
    /// </summary>
    public static void DrawFilterableCombo(string comboId, string filterId, IReadOnlyList<string> names,
        ref int selectedIndex, ref string filter, string placeholder)
    {
        string preview = selectedIndex >= 0 && selectedIndex < names.Count ? names[selectedIndex] : placeholder;
        if (!ImGui.BeginCombo(comboId, preview)) return;

        if (ImGui.IsWindowAppearing())
        {
            filter = "";
            ImGui.SetKeyboardFocusHere();
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText(filterId, ref filter, 128);
        ImGui.Separator();

        bool any = false;
        for (int i = 0; i < names.Count; i++)
        {
            if (!string.IsNullOrEmpty(filter) && !names[i].Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            any = true;
            bool sel = selectedIndex == i;
            if (ImGui.Selectable(names[i], sel))
            {
                selectedIndex = i;
                filter        = "";
            }
            if (sel) ImGui.SetItemDefaultFocus();
        }

        if (!any) ImGui.TextDisabled("No matches.");
        ImGui.EndCombo();
    }

    /// <summary>
    /// Uppercase section heading (matches the mockup's small-caps style) with an optional circled
    /// "?" that reveals <paramref name="tooltip"/> on hover, replacing a permanently-visible
    /// <c>TextDisabled</c> paragraph so a two-column layout has room to breathe. Pass <c>null</c>
    /// to render a plain heading with no icon. Every section title in the plugin should go through
    /// this, even the ones with no tooltip yet, so casing stays consistent from one place.
    /// </summary>
    public static void SectionLabel(string text, string? tooltip)
    {
        ImGui.TextUnformatted(text.ToUpperInvariant());
        if (tooltip is null) return;

        ImGui.SameLine();
        DrawHelpIcon(tooltip);
    }

    /// <summary>
    /// Small colored dot for an inline status readout (e.g. next to the Morphing status text).
    /// Reserves its own width and advances the cursor, so a following <c>ImGui.SameLine()</c> +
    /// text picks up right after it.
    /// </summary>
    public static void StatusDot(Vector4 color, string? tooltip = null)
    {
        const float r = 4f;
        float lineH  = ImGui.GetTextLineHeight();
        var   pos    = ImGui.GetCursorScreenPos();
        var   center = new Vector2(pos.X + r, pos.Y + lineH / 2f);

        ImGui.Dummy(new Vector2(r * 2f, lineH));
        bool hovered = ImGui.IsItemHovered();

        ImGui.GetWindowDrawList().AddCircleFilled(center, r, ImGui.GetColorU32(color));

        if (tooltip is null || !hovered) return;
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(tooltip);
        ImGui.EndTooltip();
    }

    /// <summary>Small circled "?", the conventional tooltip-info glyph, drawn by hand since ImGui has no built-in icon font here.</summary>
    internal static void DrawHelpIcon(string tooltip)
    {
        const float r = 7f;
        float lineH = ImGui.GetTextLineHeight();
        var   pos   = ImGui.GetCursorScreenPos();
        var   center = new Vector2(pos.X + r, pos.Y + lineH / 2f);

        ImGui.Dummy(new Vector2(r * 2f, lineH));
        bool hovered = ImGui.IsItemHovered();

        var  drawList = ImGui.GetWindowDrawList();
        uint col      = ImGui.GetColorU32(hovered ? Theme.Accent : Theme.TextMuted);
        drawList.AddCircle(center, r, col, 0, 1.3f);

        var qSize = ImGui.CalcTextSize("?");
        drawList.AddText(center - qSize / 2f, col, "?");

        if (!hovered) return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 20f);
        ImGui.TextUnformatted(tooltip);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    /// <summary>
    /// Filled, accent-colored button for the primary action in a row, e.g. Apply/Request/Resume/Bind.
    /// There's exactly one of these per control row so it reads as "the thing that moves this forward".
    /// </summary>
    public static bool PrimaryButton(string label, Vector2 size)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Button, Theme.Accent)
            .Push(ImGuiCol.ButtonHovered, Theme.AccentHover)
            .Push(ImGuiCol.ButtonActive, Theme.AccentActive)
            .Push(ImGuiCol.Text, Theme.AccentInk);
        return ImGui.Button(label, size);
    }

    /// <summary>Red-tinted button for a reset/undo action, e.g. Reset to Origin/Active.</summary>
    public static bool DangerButton(string label, Vector2 size)
    {
        var bg  = Vector4.Lerp(Theme.PanelBgHover, Theme.Danger, 0.16f);
        var bgHover = Vector4.Lerp(Theme.PanelBgHover, Theme.Danger, 0.30f);
        using var c = ImRaii.PushColor(ImGuiCol.Button, bg)
            .Push(ImGuiCol.ButtonHovered, bgHover)
            .Push(ImGuiCol.ButtonActive, Theme.Danger)
            .Push(ImGuiCol.Text, Theme.Danger);
        return ImGui.Button(label, size);
    }

    /// <summary>
    /// Row of "pill" buttons standing in for a native <c>RadioButton</c> row: the selected option is
    /// drawn with <see cref="PrimaryButton"/>, the rest plain. When every label's natural text width
    /// fits the available row, all buttons stretch to share it evenly (the flex-row look from the
    /// mockup). When it doesn't (a narrow window, a long label like "Ease In-Out"), each button is
    /// sized to its own text instead of being crushed below it, and the row wraps to a new line
    /// rather than clipping. Wrap the call in <c>ImRaii.Disabled</c> at the call site, same as the
    /// RadioButton rows it replaces.
    /// </summary>
    /// <param name="maxWidth">
    /// Overrides the row width the group flexes/wraps against. Needed inside a <see cref="Card"/>,
    /// where the raw content region extends past the card's own right inset (only one side of that
    /// inset is reflected by <c>Indent</c>) — pass the card's <c>innerWidth</c> (adjusted for
    /// whatever a preceding same-line label already consumed) there instead of leaving this null.
    /// </param>
    public static bool ButtonGroup(string idPrefix, IReadOnlyList<string> labels, ref int selected, float? maxWidth = null)
    {
        bool  changed   = false;
        float avail     = maxWidth ?? ImGui.GetContentRegionAvail().X;
        float spacing   = ImGui.GetStyle().ItemSpacing.X;
        float framePadX = ImGui.GetStyle().FramePadding.X;

        var   natural = new float[labels.Count];
        float totalNatural = 0f;
        for (int i = 0; i < labels.Count; i++)
        {
            natural[i]    = ImGui.CalcTextSize(labels[i]).X + framePadX * 2f;
            totalNatural += natural[i] + (i > 0 ? spacing : 0f);
        }

        bool fitsEvenly = totalNatural <= avail;
        float evenWidth = fitsEvenly ? (avail - spacing * (labels.Count - 1)) / labels.Count : 0f;

        float rowUsed = 0f;
        for (int i = 0; i < labels.Count; i++)
        {
            float w = fitsEvenly ? evenWidth : natural[i];

            bool startNewRow = i > 0 && !fitsEvenly && rowUsed + spacing + w > avail;
            if (i > 0 && !startNewRow) ImGui.SameLine();
            rowUsed = startNewRow ? w : rowUsed + (i > 0 ? spacing : 0f) + w;

            string id = $"{labels[i]}##{idPrefix}{i}";
            bool clicked = i == selected
                ? PrimaryButton(id, new Vector2(w, 0))
                : ImGui.Button(id, new Vector2(w, 0));

            if (clicked && i != selected)
            {
                selected = i;
                changed  = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// A <c>SliderFloat</c> repainted with a violet→teal gradient fill and a glowing white grab;
    /// the native widget still owns dragging/keyboard input and the centered value text, only its
    /// paint is replaced. The fill and grab are both drawn from the pre-drag value, before the
    /// native call, so the format text (drawn by the native widget itself) always renders on top of
    /// them instead of being covered by the grab's glow. Respects a preceding <c>SetNextItemWidth</c>
    /// (via <c>CalcItemWidth</c>) instead of always taking the full row, same as the native widget
    /// it replaces, since some call sites reserve room for a trailing label.
    /// </summary>
    public static bool GradientSlider(string label, ref float value, float min, float max, string format,
        ImGuiSliderFlags flags = ImGuiSliderFlags.None)
    {
        var   drawList = ImGui.GetWindowDrawList();
        float width    = ImGui.CalcItemWidth();
        float height   = ImGui.GetFrameHeight();
        var   rectMin  = ImGui.GetCursorScreenPos();
        var   rectMax  = rectMin + new Vector2(width, height);
        float rounding = ImGui.GetStyle().FrameRounding;
        bool  logarithmic = (flags & ImGuiSliderFlags.Logarithmic) != 0 && min > 0f && max > 0f;
        float frac        = SliderFraction(value, min, max, logarithmic); // pre-drag value

        PaintTrackAndFill(drawList, rectMin, rectMax, rounding, frac);

        // Grab glow/dot painted BEFORE the native call (not after), so the native centered value
        // text, drawn inside SliderFloat itself, always lands on top of it instead of being
        // covered by the grab; same pre-drag-position tradeoff PaintTrackAndFill already makes.
        float grabR = height / 2f - 1f;
        // Inset by the grab's own radius (matching how ImGui positions its own, invisible, grab)
        // so the circle stays fully inside the frame at frac 0/1 instead of being sliced in half
        // by the rounded border.
        float grabX = rectMin.X + grabR + (rectMax.X - rectMin.X - 2f * grabR) * frac;
        var   grabCenter = new Vector2(grabX, (rectMin.Y + rectMax.Y) / 2f);

        for (int i = 3; i >= 1; i--)
            drawList.AddCircleFilled(grabCenter, grabR + i * 2.5f,
                ImGui.GetColorU32(new Vector4(Theme.Accent.X, Theme.Accent.Y, Theme.Accent.Z, 0.05f)));
        drawList.AddCircleFilled(grabCenter, grabR, ImGui.GetColorU32(Vector4.One));
        drawList.AddCircle(grabCenter, grabR, ImGui.GetColorU32(new Vector4(Theme.Accent.X, Theme.Accent.Y, Theme.Accent.Z, 0.5f)), 0, 2f);

        // Dark pill behind where the native value text will render, sized to the widest string this
        // slider can show (formatted at `max`) so it doesn't jitter as the live value's width
        // changes. Keeps the near-white text legible over the white grab (and the pastel fill)
        // without recoloring the grab itself, which looked out of place against the violet→teal
        // palette (tried Theme.AccentActive, too saturated a purple next to the pastel gradient).
        string  sample     = FormatSliderValueForSizing(format, max);
        var     textSize   = ImGui.CalcTextSize(sample);
        var     padding    = new Vector2(6f, 2f);
        var     bgMin      = new Vector2(rectMin.X + (width - textSize.X) / 2f - padding.X,
            rectMin.Y + (height - textSize.Y) / 2f - padding.Y);
        var     bgMax      = bgMin + textSize + padding * 2f;
        drawList.AddRectFilled(bgMin, bgMax, ImGui.GetColorU32(new Vector4(Theme.TrackBg.X, Theme.TrackBg.Y, Theme.TrackBg.Z, 0.75f)),
            (bgMax.Y - bgMin.Y) / 2f);

        bool changed;
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero)
                   .Push(ImGuiCol.FrameBgHovered, Vector4.Zero)
                   .Push(ImGuiCol.FrameBgActive, Vector4.Zero)
                   .Push(ImGuiCol.SliderGrab, Vector4.Zero)
                   .Push(ImGuiCol.SliderGrabActive, Vector4.Zero))
        {
            changed = ImGui.SliderFloat(label, ref value, min, max, format, flags);
        }

        return changed;
    }

    /// <summary>
    /// Renders a printf-style <c>"%.Nf"</c> float format (the only spec <see cref="GradientSlider"/>
    /// callers use, e.g. <c>"%.2f s"</c>) for sizing the value-text backdrop only, doesn't need to
    /// match the native C-runtime formatting character-for-character, just close enough in width.
    /// </summary>
    private static string FormatSliderValueForSizing(string cFormat, float value) =>
        Regex.Replace(cFormat, @"%\.(\d+)f", m => value.ToString("F" + m.Groups[1].Value));

    /// <summary>
    /// Grab-position fraction for <see cref="GradientSlider"/>, matching ImGui's own internal
    /// logarithmic mapping (<c>log(value/min) / log(max/min)</c>) when the Logarithmic flag is set,
    /// so the hand-painted fill/grab line up with where the native (invisible) slider actually is.
    /// </summary>
    private static float SliderFraction(float value, float min, float max, bool logarithmic)
    {
        if (logarithmic)
            return Math.Clamp(MathF.Log(Math.Max(value, min) / min) / MathF.Log(max / min), 0f, 1f);
        return Math.Clamp((value - min) / (max - min), 0f, 1f);
    }

    /// <summary>
    /// <c>ImGui.ProgressBar</c> repainted with the same violet→teal gradient fill as
    /// <see cref="GradientSlider"/>, for morph/gauge progress everywhere it's shown. Size follows
    /// the same -1-means-"fill available space" convention as the native widget.
    /// </summary>
    public static void GradientProgressBar(float fraction, Vector2 size, string? overlay)
    {
        var   drawList  = ImGui.GetWindowDrawList();
        float width     = size.X > 0f ? size.X : ImGui.GetContentRegionAvail().X;
        float height    = size.Y > 0f ? size.Y : ImGui.GetFrameHeight();
        var   rectMin   = ImGui.GetCursorScreenPos();
        var   rectMax   = rectMin + new Vector2(width, height);
        float rounding  = ImGui.GetStyle().FrameRounding;

        PaintTrackAndFill(drawList, rectMin, rectMax, rounding, Math.Clamp(fraction, 0f, 1f));

        if (!string.IsNullOrEmpty(overlay))
        {
            var textSize = ImGui.CalcTextSize(overlay);
            var textPos  = new Vector2(rectMin.X + (width - textSize.X) / 2f, rectMin.Y + (height - textSize.Y) / 2f);
            drawList.AddText(textPos, ImGui.GetColorU32(Theme.Text), overlay);
        }

        ImGui.Dummy(new Vector2(width, height));
    }

    /// <summary>
    /// Track + violet→teal gradient fill shared by <see cref="GradientSlider"/> and
    /// <see cref="GradientProgressBar"/>. The track uses <see cref="Theme.TrackBg"/> (darker than
    /// both the window and card backgrounds it sits on) plus a border, so it reads as a bar rather
    /// than disappearing against whatever's behind it.
    /// <para/>
    /// <c>AddRectFilledMultiColor</c> has no rounding parameter, so a plain gradient rect would
    /// show square corners poking past the track's rounded ends. Fixed with solid-color rounded
    /// caps (in the fill's own start/end colors, so the seam is invisible) at each visible edge,
    /// with a flat gradient body between them; caps shrink instead of overlapping when the fill is
    /// too narrow for both.
    /// </summary>
    private static void PaintTrackAndFill(ImDrawListPtr drawList, Vector2 rectMin, Vector2 rectMax, float rounding, float frac)
    {
        drawList.AddRectFilled(rectMin, rectMax, ImGui.GetColorU32(Theme.TrackBg), rounding);

        float fillWidth = (rectMax.X - rectMin.X) * frac;
        if (fillWidth > 0.5f)
        {
            var   fillMax = new Vector2(rectMin.X + fillWidth, rectMax.Y);
            float cap     = Math.Min(rounding, fillWidth / 2f);
            uint  colA    = ImGui.GetColorU32(Theme.Accent);
            uint  colB    = ImGui.GetColorU32(Theme.Accent2);

            if (cap > 0.5f)
            {
                drawList.AddRectFilled(rectMin, new Vector2(rectMin.X + cap, rectMax.Y), colA, rounding, ImDrawFlags.RoundCornersLeft);
                drawList.AddRectFilled(new Vector2(fillMax.X - cap, rectMin.Y), fillMax, colB, rounding, ImDrawFlags.RoundCornersRight);
            }

            var bodyMin = new Vector2(rectMin.X + cap, rectMin.Y);
            var bodyMax = new Vector2(fillMax.X - cap, rectMax.Y);
            if (bodyMax.X > bodyMin.X)
                drawList.AddRectFilledMultiColor(bodyMin, bodyMax, colA, colB, colB, colA);
        }

        drawList.AddRect(rectMin, rectMax, ImGui.GetColorU32(Theme.Border), rounding);
    }

    /// <summary>
    /// Draws <paramref name="content"/> inside a padded, rounded, bordered panel matching the
    /// mockup's status-card, used for the morph progress bar + action buttons so they read as one
    /// unit instead of loose widgets. <paramref name="content"/> receives the padded inner width to
    /// size its own children with (progress bar, button rows) since a plain <c>Indent</c> only
    /// insets the left edge, not the right.
    /// <para/>
    /// The background can't be drawn before the content because its height isn't known until the
    /// content has been drawn, so this uses draw-list channel splitting: content renders into
    /// channel 1, the background rect renders into channel 0 afterward, and <c>ChannelsMerge</c>
    /// puts channel 0 back underneath channel 1 regardless of draw order.
    /// </summary>
    public static void Card(Action<float> content)
    {
        const float pad = 12f;

        var   drawList   = ImGui.GetWindowDrawList();
        float rounding   = ImGui.GetStyle().FrameRounding;
        float fullWidth  = ImGui.GetContentRegionAvail().X;
        float innerWidth = fullWidth - pad * 2f;
        var   outerMin   = ImGui.GetCursorScreenPos();

        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.Dummy(new Vector2(1, pad));
        ImGui.Indent(pad);
        content(innerWidth);
        ImGui.Unindent(pad);
        ImGui.Dummy(new Vector2(1, pad));

        var outerMax = new Vector2(outerMin.X + fullWidth, ImGui.GetCursorScreenPos().Y);

        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(outerMin, outerMax, ImGui.GetColorU32(Theme.PanelBg), rounding);
        drawList.AddRect(outerMin, outerMax, ImGui.GetColorU32(Theme.Border), rounding);
        drawList.ChannelsMerge();
    }
}
