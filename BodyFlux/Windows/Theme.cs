using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace BodyFlux.Windows;

/// <summary>
/// BodyFlux's ImGui look. The accent is lifted from the GitHub README hero so the in-game window and
/// the repo page read as the same product; Accent2 is reserved for network/live state (Sync badge,
/// gradients) rather than general decoration. Pushed once per window frame via <see cref="PushColors"/>/
/// <see cref="PushStyleVars"/>, called from <see cref="MainWindow.PreDraw"/>, not per widget, so
/// individual tab views only need to override color for a second semantic meaning
/// (success/warning/danger), which they already do throughout.
/// </summary>
internal static class Theme
{
    public static readonly Vector4 WindowBg     = Rgb(0x16, 0x13, 0x20);
    public static readonly Vector4 PanelBg      = Rgb(0x20, 0x1B, 0x2E);
    public static readonly Vector4 PanelBgHover = Rgb(0x28, 0x21, 0x41);
    // Darker than both WindowBg and PanelBg on purpose, since sliders/progress bars live directly
    // on either surface (loose in the two-column layout, or inside a Card); their track needs to
    // read as a sunken well against whichever one is behind it, not blend into it.
    public static readonly Vector4 TrackBg      = Rgb(0x0d, 0x0b, 0x16);
    public static readonly Vector4 Border       = Rgb(0x36, 0x2F, 0x4A);
    public static readonly Vector4 Text         = Rgb(0xF1, 0xEE, 0xFB);
    public static readonly Vector4 TextMuted    = Rgb(0x94, 0x8D, 0xB0);

    public static readonly Vector4 Accent       = Rgb(0x9D, 0x7C, 0xF5); // from the GitHub README hero
    public static readonly Vector4 AccentHover  = Rgb(0xB3, 0x9A, 0xFF);
    public static readonly Vector4 AccentActive = Rgb(0x7C, 0x5C, 0xE0);
    public static readonly Vector4 AccentInk    = Rgb(0xF8, 0xF6, 0xFF); // text drawn on top of Accent

    public static readonly Vector4 Accent2      = Rgb(0x4F, 0xD8, 0xC4); // network/live state only

    public static readonly Vector4 Success      = Rgb(0x3F, 0xB9, 0x50);
    public static readonly Vector4 Warning      = Rgb(0xE2, 0x70, 0x3A);
    public static readonly Vector4 Danger       = Rgb(0xFF, 0x75, 0x6F); // bumped from #F0625F: 4.19:1 failed AA, this is 5.26:1

    private const float WindowRounding    = 16f;
    private const float ChildRounding     = 8f;
    private const float PopupRounding     = 8f;
    private const float FrameRounding     = 8f;
    private const float TabRounding       = 7f;
    private const float GrabRounding      = 12f;
    private const float ScrollbarRounding = 12f;
    private const float WindowBorderSize  = 1f;
    private const float FrameBorderSize   = 1f;

    private static Vector4 Rgb(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, 1f);

    // Declared after the color fields above so field-initializer order picks up their final values.
    private static readonly (ImGuiCol Col, Vector4 Value)[] Colors =
    [
        (ImGuiCol.WindowBg,         WindowBg),
        (ImGuiCol.ChildBg,          WindowBg),
        (ImGuiCol.PopupBg,          WindowBg),
        (ImGuiCol.TitleBg,          WindowBg),
        (ImGuiCol.TitleBgActive,    WindowBg),
        (ImGuiCol.TitleBgCollapsed, WindowBg),
        (ImGuiCol.Border,           Border),
        (ImGuiCol.Separator,        Border),
        (ImGuiCol.FrameBg,          PanelBg),
        (ImGuiCol.FrameBgHovered,   PanelBgHover),
        (ImGuiCol.FrameBgActive,    PanelBgHover),
        (ImGuiCol.Text,             Text),
        (ImGuiCol.TextDisabled,     TextMuted),
        (ImGuiCol.Button,           PanelBgHover),
        (ImGuiCol.ButtonHovered,    AccentHover),
        (ImGuiCol.ButtonActive,     AccentActive),
        (ImGuiCol.Header,           PanelBgHover),
        (ImGuiCol.HeaderHovered,    AccentHover),
        (ImGuiCol.HeaderActive,     AccentActive),
        (ImGuiCol.Tab,              PanelBg),
        (ImGuiCol.TabHovered,       PanelBgHover),
        (ImGuiCol.TabActive,        PanelBgHover),
        (ImGuiCol.CheckMark,        Accent),
        (ImGuiCol.SliderGrab,       Accent),
        (ImGuiCol.SliderGrabActive, AccentHover),
        (ImGuiCol.PlotHistogram,    Accent), // ProgressBar's fill reuses this color
    ];

    private static readonly (ImGuiStyleVar Var, float Value)[] FloatVars =
    [
        (ImGuiStyleVar.WindowRounding,    WindowRounding),
        (ImGuiStyleVar.ChildRounding,     ChildRounding),
        (ImGuiStyleVar.PopupRounding,     PopupRounding),
        (ImGuiStyleVar.FrameRounding,     FrameRounding),
        (ImGuiStyleVar.TabRounding,       TabRounding),
        (ImGuiStyleVar.GrabRounding,      GrabRounding),
        (ImGuiStyleVar.ScrollbarRounding, ScrollbarRounding),
        (ImGuiStyleVar.WindowBorderSize,  WindowBorderSize),
        (ImGuiStyleVar.FrameBorderSize,   FrameBorderSize),
    ];

    private static readonly (ImGuiStyleVar Var, Vector2 Value)[] VecVars =
    [
        (ImGuiStyleVar.FramePadding, new Vector2(10, 6)),
    ];

    /// <summary>
    /// Gutter between the window edge and tab content, pushed separately in MainWindow.Draw()
    /// (not part of <see cref="PushStyleVars"/>) because the custom title bar draws at the raw
    /// window edge on purpose and would be thrown off by an ambient WindowPadding change.
    /// </summary>
    public static readonly Vector2 ContentPadding = new(16f, 10f);

    public static void PushColors()
    {
        foreach (var (col, value) in Colors)
            ImGui.PushStyleColor(col, value);
    }

    public static void PopColors() => ImGui.PopStyleColor(Colors.Length);

    public static void PushStyleVars()
    {
        foreach (var (var, value) in FloatVars)
            ImGui.PushStyleVar(var, value);
        foreach (var (var, value) in VecVars)
            ImGui.PushStyleVar(var, value);
    }

    public static void PopStyleVars() => ImGui.PopStyleVar(FloatVars.Length + VecVars.Length);

    // ── Custom title bar ─────────────────────────────────────────────────────
    // MainWindow uses NoTitleBar, so everything below (background, icon, title, version, status
    // dots, the Sync pill, drag-to-move, and the close button) is drawn and hit-tested by hand.

    public const float TitleBarHeight = 44f;

    private static readonly string IconPath =
        Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName!, "images", "icon.png");

    private static bool _iconPathLogged;
    private static int  _iconNullFrames;

    /// <summary>
    /// BodyFlux's own icon (images/icon.png). <c>GetFromFile</c> is a shared/cached lookup keyed by
    /// path, not a fresh disk read, so it's safe to call every frame. Null until the texture has
    /// finished loading (or if it fails to load at all). Logs once so a load failure is visible in
    /// /xllog instead of just silently never drawing.
    /// </summary>
    public static IDalamudTextureWrap? Icon
    {
        get
        {
            if (!_iconPathLogged)
            {
                _iconPathLogged = true;
                Plugin.Log.Info($"[Theme] Icon path: {IconPath} (file exists: {File.Exists(IconPath)})");
            }

            var wrap = Plugin.TextureProvider.GetFromFile(IconPath).GetWrapOrDefault();
            if (wrap is null)
            {
                _iconNullFrames++;
                if (_iconNullFrames == 180) // ~3s at 60fps, well past a normal async load
                    Plugin.Log.Warning("[Theme] Icon still hasn't loaded after 180 frames, GetFromFile is likely failing.");
            }
            return wrap;
        }
    }

    /// <summary>
    /// Draws BodyFlux's custom title bar: icon, bold/big title, muted version tag, small
    /// Customize+/Brio status dots, an optional Sync status pill, drag-to-move, a collapse chevron,
    /// and a red close button. Call once per frame at the very top of <c>Draw()</c> (right after
    /// <c>Begin()</c>, so window position/size are known). Centralizing the dependency dots here
    /// means individual tabs (Player, Gpose) don't each need their own copy.
    /// </summary>
    /// <param name="collapsed">Current collapsed state, so the chevron can point the right way.</param>
    /// <returns>
    /// <c>(closeClicked, collapseToggled)</c>: the caller applies these (<c>IsOpen = false</c>, flip
    /// its own collapsed flag) since this method doesn't own that state.
    /// </returns>
    public static (bool closeClicked, bool collapseToggled, bool menuClicked) DrawCustomTitleBar(
        string title, string version, bool syncLive, int peerCount, bool collapsed)
    {
        var   drawList = ImGui.GetWindowDrawList();
        var   winPos   = ImGui.GetWindowPos();
        float width    = ImGui.GetWindowWidth();
        var   barMin   = winPos;
        var   barMax   = winPos + new Vector2(width, TitleBarHeight);

        // Background (slightly darker than the window body) + a hairline bottom border.
        var barBg = new Vector4(WindowBg.X * 0.82f, WindowBg.Y * 0.82f, WindowBg.Z * 0.82f, 1f);
        drawList.AddRectFilled(barMin, barMax, ImGui.GetColorU32(barBg), 16f, ImDrawFlags.RoundCornersTop);
        drawList.AddLine(new Vector2(barMin.X, barMax.Y), new Vector2(barMax.X, barMax.Y), ImGui.GetColorU32(Border));

        // All three buttons' rects are needed up front, since the Sync badge/status dots lay out
        // against the leftmost of them (the menu button), and the drag zone below needs to stop
        // short of the whole row instead of overlapping it.
        const float btnSize = 22f;
        const float btnGap  = 8f;
        const float edgePad = 14f;
        var closeMin    = new Vector2(barMax.X - edgePad - btnSize, barMin.Y + (TitleBarHeight - btnSize) / 2f);
        var closeMax    = closeMin + new Vector2(btnSize, btnSize);
        var collapseMin = new Vector2(closeMin.X - btnGap - btnSize, closeMin.Y);
        var collapseMax = collapseMin + new Vector2(btnSize, btnSize);
        // Opens the Pin/Clickthrough/Opacity menu — the equivalent of Dalamud's native title-bar
        // menu, which stopped drawing itself once this window switched to NoTitleBar (see MainWindow).
        var menuMin     = new Vector2(collapseMin.X - btnGap - btnSize, closeMin.Y);
        var menuMax     = menuMin + new Vector2(btnSize, btnSize);

        // Drag zone stops short of the button row instead of overlapping it: overlapping
        // InvisibleButtons made the close button unreliable to click (the drag zone could end up
        // owning the press), so the two no longer share any pixels at all.
        float dragWidth = MathF.Max(0f, menuMin.X - btnGap - barMin.X);
        ImGui.SetCursorScreenPos(barMin);
        ImGui.InvisibleButton("##titlebar_drag", new Vector2(dragWidth, TitleBarHeight));
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            ImGui.SetWindowPos(winPos + ImGui.GetIO().MouseDelta);

        // ── Icon ─────────────────────────────────────────────────────────────
        float iconSize = TitleBarHeight - 18f;
        var   iconPos  = new Vector2(barMin.X + 14f, barMin.Y + (TitleBarHeight - iconSize) / 2f);
        float textX    = iconPos.X;

        var icon = Icon;
        if (icon is not null)
        {
            drawList.AddImageRounded(icon.Handle, iconPos, iconPos + new Vector2(iconSize, iconSize),
                Vector2.Zero, Vector2.One, 0xFFFFFFFFu, iconSize / 2f);
            textX = iconPos.X + iconSize + 10f;
        }

        // ── Title: bold via a 1px double-draw (no bold font loaded), big via a temporary window
        //    font scale (draw-list text at the default scale would ignore SetWindowFontScale, but
        //    the plain CalcTextSize/AddText pair we use here both read off it like any other text) ──
        ImGui.SetWindowFontScale(1.45f);
        var  titleSize = ImGui.CalcTextSize(title);
        var  titlePos  = new Vector2(textX, barMin.Y + (TitleBarHeight - titleSize.Y) / 2f);
        uint titleCol  = ImGui.GetColorU32(Text);
        drawList.AddText(titlePos, titleCol, title);
        drawList.AddText(titlePos + new Vector2(0.7f, 0f), titleCol, title);
        ImGui.SetWindowFontScale(1f);

        // ── Version, muted, right after the title at normal size ───────────────
        var versionPos = new Vector2(textX + titleSize.X + 8f, barMin.Y + (TitleBarHeight - ImGui.GetFontSize()) / 2f);
        drawList.AddText(versionPos, ImGui.GetColorU32(TextMuted), version);

        // ── Sync badge, right-aligned against the button row. Customize+/Brio status used to show
        //    as small dots here too, but there wasn't room for full text, so they moved down to
        //    the custom footer (see DrawCustomFooter) instead. ──
        if (syncLive)
        {
            string text     = $"SYNC · {peerCount}";
            var    textSize = ImGui.CalcTextSize(text);
            float  height   = 22f;
            float  pillW    = textSize.X + 24f;
            float  badgeRight = menuMin.X - 14f;
            var    max      = new Vector2(badgeRight, barMin.Y + (TitleBarHeight + height) / 2f);
            var    min       = new Vector2(max.X - pillW, barMin.Y + (TitleBarHeight - height) / 2f);

            uint badgeBg     = ImGui.GetColorU32(new Vector4(Accent2.X, Accent2.Y, Accent2.Z, 0.18f));
            uint badgeBorder = ImGui.GetColorU32(new Vector4(Accent2.X, Accent2.Y, Accent2.Z, 0.45f));
            drawList.AddRectFilled(min, max, badgeBg, height / 2f);
            drawList.AddRect(min, max, badgeBorder, height / 2f);

            var dotCenter = new Vector2(min.X + 10f, min.Y + height / 2f);
            drawList.AddCircleFilled(dotCenter, 3f, ImGui.GetColorU32(Accent2));

            var textPos = new Vector2(dotCenter.X + 8f, min.Y + (height - textSize.Y) / 2f);
            drawList.AddText(textPos, ImGui.GetColorU32(Accent2), text);
        }

        // ── Menu button (three dashes) — opens the Pin/Clickthrough/Opacity menu ────
        ImGui.SetCursorScreenPos(menuMin);
        bool menuClicked  = ImGui.InvisibleButton("##titlebar_menu", new Vector2(btnSize, btnSize));
        bool menuHovered  = ImGui.IsItemHovered();

        if (menuHovered)
            drawList.AddCircleFilled((menuMin + menuMax) / 2f, btnSize / 2f, ImGui.GetColorU32(PanelBgHover));

        var  menuCenter = (menuMin + menuMax) / 2f;
        uint menuCol     = ImGui.GetColorU32(TextMuted);
        const float dashW = 5.5f, dashGap = 3.5f;
        for (int i = -1; i <= 1; i++)
        {
            float y = menuCenter.Y + i * dashGap;
            drawList.AddLine(new Vector2(menuCenter.X - dashW, y), new Vector2(menuCenter.X + dashW, y), menuCol, 1.5f);
        }

        // ── Collapse chevron, points down to collapse, up to expand ────────────
        ImGui.SetCursorScreenPos(collapseMin);
        bool collapseClicked = ImGui.InvisibleButton("##titlebar_collapse", new Vector2(btnSize, btnSize));
        bool collapseHovered = ImGui.IsItemHovered();

        if (collapseHovered)
            drawList.AddCircleFilled((collapseMin + collapseMax) / 2f, btnSize / 2f, ImGui.GetColorU32(PanelBgHover));

        var  chevCenter = (collapseMin + collapseMax) / 2f;
        uint chevCol     = ImGui.GetColorU32(TextMuted);
        const float chevW = 5f, chevH = 3f;
        if (collapsed) // pointing up: ⌃
            drawList.AddTriangleFilled(
                chevCenter + new Vector2(-chevW, chevH), chevCenter + new Vector2(chevW, chevH), chevCenter + new Vector2(0, -chevH), chevCol);
        else // pointing down: ⌄
            drawList.AddTriangleFilled(
                chevCenter + new Vector2(-chevW, -chevH), chevCenter + new Vector2(chevW, -chevH), chevCenter + new Vector2(0, chevH), chevCol);

        // ── Close button ─────────────────────────────────────────────────────
        ImGui.SetCursorScreenPos(closeMin);
        bool closeClicked  = ImGui.InvisibleButton("##titlebar_close", new Vector2(btnSize, btnSize));
        bool closeHovered  = ImGui.IsItemHovered();

        if (closeHovered)
            drawList.AddCircleFilled((closeMin + closeMax) / 2f, btnSize / 2f,
                ImGui.GetColorU32(new Vector4(Danger.X, Danger.Y, Danger.Z, 0.18f)));

        const float xPad = 6.5f;
        uint xCol = ImGui.GetColorU32(Danger);
        drawList.AddLine(closeMin + new Vector2(xPad, xPad), closeMax - new Vector2(xPad, xPad), xCol, 1.7f);
        drawList.AddLine(new Vector2(closeMax.X - xPad, closeMin.Y + xPad), new Vector2(closeMin.X + xPad, closeMax.Y - xPad), xCol, 1.7f);

        // Hand the cursor back to normal window-content flow, below the whole bar. Inset by the
        // active WindowPadding (not the raw window edge) so the tab bar and everything after it
        // actually get the padding pushed in MainWindow.PreDraw, the same as content elsewhere.
        var contentPad = ImGui.GetStyle().WindowPadding;
        ImGui.SetCursorScreenPos(new Vector2(winPos.X + contentPad.X, barMax.Y + 1f));

        return (closeClicked, collapseClicked, menuClicked);
    }

    // ── Custom footer ─────────────────────────────────────────────────────────
    // Sits below the tab content on every tab (unlike the old per-tab command hint / dependency
    // banners, which only Player's Single tab and Gpose's gated sections happened to draw). The
    // caller reserves this height by sizing its content Child to (0, -FooterHeight) before calling
    // this, then calls DrawCustomFooter once the Child has ended.

    public const float FooterHeight = 38f;

    /// <summary>
    /// Draws BodyFlux's custom footer: full-text Customize+/Brio dependency status (promoted here
    /// from the title bar, which only had room for a bare dot) and the "/bodyflux" chat-command
    /// reminder, right-aligned. Call once per frame, after the tab content's Child has ended.
    /// </summary>
    public static void DrawCustomFooter(bool cpOk, bool brioOk)
    {
        var drawList = ImGui.GetWindowDrawList();
        var winPos   = ImGui.GetWindowPos();
        var winSize  = ImGui.GetWindowSize();
        var barMax   = winPos + winSize;
        var barMin   = new Vector2(winPos.X, barMax.Y - FooterHeight);

        // Same darkened background + hairline border treatment as the title bar, just anchored to
        // the bottom corners instead of the top ones.
        var barBg = new Vector4(WindowBg.X * 0.82f, WindowBg.Y * 0.82f, WindowBg.Z * 0.82f, 1f);
        drawList.AddRectFilled(barMin, barMax, ImGui.GetColorU32(barBg), 16f, ImDrawFlags.RoundCornersBottom);
        drawList.AddLine(barMin, new Vector2(barMax.X, barMin.Y), ImGui.GetColorU32(Border));

        const float edgePad = 18f;
        float y = barMin.Y + FooterHeight / 2f;

        float x = barMin.X + edgePad;
        x = DrawFooterStatus(drawList, new Vector2(x, y), cpOk, "Customize+ Enabled", "Customize+ Not Detected");
        DrawFooterStatus(drawList, new Vector2(x + 18f, y), brioOk, "Brio Enabled", "Brio Not Detected");

        const string cmd = "/bodyflux";
        var cmdSize = ImGui.CalcTextSize(cmd);
        var cmdPos  = new Vector2(barMax.X - edgePad - cmdSize.X, y - cmdSize.Y / 2f);
        drawList.AddText(cmdPos, ImGui.GetColorU32(TextMuted), cmd);
    }

    /// <summary>Draws one "dot + label" dependency status entry, vertically centered on <paramref name="start"/>.Y. Returns the X just past the label's right edge, for chaining the next entry.</summary>
    private static float DrawFooterStatus(ImDrawListPtr drawList, Vector2 start, bool ok, string onLabel, string offLabel)
    {
        const float r = 4f;
        var dotCenter = new Vector2(start.X + r, start.Y);
        drawList.AddCircleFilled(dotCenter, r, ImGui.GetColorU32(ok ? Success : Danger));

        string label    = ok ? onLabel : offLabel;
        var    textPos  = new Vector2(dotCenter.X + r + 6f, start.Y - ImGui.GetTextLineHeight() / 2f);
        drawList.AddText(textPos, ImGui.GetColorU32(TextMuted), label);

        return textPos.X + ImGui.CalcTextSize(label).X;
    }
}
