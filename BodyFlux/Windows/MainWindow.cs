using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace BodyFlux.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private bool _refreshedOnOpen;

    // ── GPose tab-management state ────────────────────────────────────────────
    private bool _wasInGpose;
    private bool _requestPlayerTabFocus;

    // Owned here (not by a tab view) because it must be drawn at the top level so an open
    // dialog survives tab switches; shared into the Brio tab for the MCDF pickers.
    private readonly FileDialogManager _fileDialog = new();

    // ── Tab views (extracted renderers) ───────────────────────────────────────
    private readonly Tabs.SyncTabView      _syncTab;
    private readonly Tabs.SequenceListView _seqList;
    private readonly Tabs.PlayerTabView    _playerTab;
    private readonly Tabs.BrioTabView      _brioTab;
    private readonly Tabs.KeybindsTabView  _keybindsTab;
    private readonly Tabs.EasingCurvesTabView _easingTab;

    private readonly string _versionText;

    // ── Collapse state ───────────────────────────────────────────────────────
    // ImGui's own window collapse is a title-bar feature we no longer have; this reimplements it
    // by remembering the pre-collapse size and shrinking to just the custom bar's height.
    private bool    _collapsed;
    private Vector2 _expandedSize;

    // ── Title-bar menu (Pin/Opacity) ─────────────────────────────────────────
    // Dalamud's own title-bar menu (Pin Window/Make clickthrough/Opacity/Background Blur) stops
    // drawing itself once a window sets NoTitleBar (confirmed in Dalamud's WindowHost — it's an
    // unconditional internal gate, not a flag we can opt back into). This is a hand-drawn
    // replacement for the two pieces that are safely reproducible: Pin Window (IsPinned is a public,
    // enforced Window property — setting it is sufficient) and Opacity (no public equivalent exists,
    // so this window owns and applies its own alpha). Clickthrough is deliberately left out: Dalamud
    // sets InheritNoInputs on the whole native window when it's enabled, which would swallow clicks
    // on this very button too, and there's no known public way back in short of a restart.
    private const string TitleMenuPopupId = "##BodyFluxTitleMenu";
    private float _opacity = 1f;

    private static readonly WindowSizeConstraints ExpandedConstraints = new()
    {
        MinimumSize = new Vector2(460, 480),
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
    };

    public MainWindow(Plugin plugin)
        : base("##BodyFluxMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoTitleBar)
    {
        SizeConstraints = ExpandedConstraints;
        Size          = new Vector2(500, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        this.plugin = plugin;

        var version = typeof(Plugin).Assembly.GetName().Version;
        _versionText = version is null ? "" : $"v{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";

        _syncTab   = new Tabs.SyncTabView(plugin);
        _seqList   = new Tabs.SequenceListView(plugin);
        _playerTab = new Tabs.PlayerTabView(plugin, _seqList, () => _refreshedOnOpen = false);
        _brioTab   = new Tabs.BrioTabView(plugin, _seqList, _fileDialog);
        _keybindsTab = new Tabs.KeybindsTabView(plugin);
        _easingTab   = new Tabs.EasingCurvesTabView(plugin);

        // The Multi "Apply All" config lives in the Brio tab view, so let the keybind handler
        // trigger it through here.
        plugin.Keybinds.ApplyMultiHandler = _brioTab.ApplyGroup;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        Theme.PushColors();
        Theme.PushStyleVars();
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, _opacity);
        // Pushed here (before Begin()), not mid-Draw() — WindowPadding only shapes the content region
        // of a window Begin()'d *after* the push. Pushing it mid-Draw(), as this used to, left every
        // tab that draws straight into the main window (no child wrapper) with the original tight
        // Begin()-time padding; only tabs that opened their own scrolling Child picked it up, which is
        // why controls looked flush against the window edge in some tabs but not others. The custom
        // title bar is unaffected either way — it paints via the raw window rect/draw-list, not the
        // padded content region (see Theme.DrawCustomTitleBar).
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.ContentPadding);

        // MinimumSize (480 tall) is enforced every frame Begin() runs, so a collapsed window kept
        // springing back to full height, since SetWindowSize alone can't get under a still-active
        // constraint. Swap to a tight collapsed-height constraint while collapsed instead.
        float barH = Theme.TitleBarHeight + 2f;
        SizeConstraints = _collapsed
            ? new WindowSizeConstraints { MinimumSize = new Vector2(300, barH), MaximumSize = new Vector2(float.MaxValue, barH) }
            : ExpandedConstraints;
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(2); // WindowPadding, Alpha
        Theme.PopStyleVars();
        Theme.PopColors();
    }

    public override void OnOpen()
    {
        if (!_refreshedOnOpen)
        {
            plugin.RefreshProfiles();
            _refreshedOnOpen = true;
        }
    }

    // ── Top-level draw ────────────────────────────────────────────────────────

    public override void Draw()
    {
        bool syncLive = plugin.Configuration.NetworkSyncEnabled
                     && plugin.NetworkSyncStatus == "Connected";
        var (closeClicked, collapseToggled, menuClicked) = Theme.DrawCustomTitleBar(
            "Body Flux", _versionText, syncLive, plugin.ConnectedPeers?.Count ?? 0, _collapsed,
            plugin.IsCustomizePlusAvailable, plugin.IsBrioAvailable);

        if (closeClicked)
            IsOpen = false;

        if (collapseToggled)
        {
            _collapsed = !_collapsed;
            if (_collapsed)
            {
                _expandedSize = ImGui.GetWindowSize();
                ImGui.SetWindowSize(new Vector2(_expandedSize.X, Theme.TitleBarHeight + 2f));
            }
            else
            {
                ImGui.SetWindowSize(_expandedSize);
            }
        }

        if (menuClicked)
            ImGui.OpenPopup(TitleMenuPopupId);
        DrawTitleMenu();

        if (_collapsed) return; // just the bar, skip tabs/content entirely while collapsed

        // ── GPose state-change detection ──────────────────────────────────────
        bool inGpose = plugin.IsInGPose;

        if (inGpose && !_wasInGpose)
        {
            // Just entered GPose: enable the Brio tab and reset its selections,
            // but do NOT steal focus — the user stays on whatever tab they were on.
            plugin.SelectedBrioActorIndex = -1;
            plugin.SelectedBrioDestIndex  = -1;
        }
        else if (!inGpose && _wasInGpose)
        {
            // Just exited GPose: tear down every Brio actor morph so nothing lingers, then
            // return to the Player tab. The player session is left untouched.
            plugin.ResetAllBrio();
            _requestPlayerTabFocus = true;
        }

        _wasInGpose = inGpose;

        // ── Tab bar ───────────────────────────────────────────────────────────
        using var tabBar = ImRaii.TabBar("##MainTabs");
        if (!tabBar) return;

        // Player tab — gains focus when returning from GPose
        var playerFlags = _requestPlayerTabFocus
            ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        _requestPlayerTabFocus = false;
        using (var tab = ImRaii.TabItem("Player", playerFlags))
            if (tab) _playerTab.Draw();

        // Sync tab — label turns green while actively connected.
        // NOTE: must use a scoped `using (...)` block so EndTabItem fires before the next
        // tab is declared. A bare `using var` defers EndTabItem to end of Draw(), leaving the
        // Sync item "open" and causing the following Brio tab to nest under it (and stop
        // responding to clicks) whenever Sync is the active tab.
        if (syncLive)
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 0.9f, 0.4f, 1f));
        using (var syncTab = ImRaii.TabItem("Sync"))
        {
            if (syncLive)
                ImGui.PopStyleColor(); // pop after BeginTabItem; tab content draws in normal colour
            if (syncTab) _syncTab.Draw();
        }

        // Brio tab — always visible; disabled (grey, non-clickable) outside GPose.
        // Using explicit Begin/EndDisabled rather than ImRaii.Disabled(!inGpose) because
        // the RAII wrapper always calls EndDisabled() even when disabled=false, which
        // unbalances the ImGui disabled stack and keeps the tab grey while in GPose.
        if (!inGpose) ImGui.BeginDisabled();
        using (var tab = ImRaii.TabItem("Gpose"))
            if (tab) _brioTab.Draw();
        if (!inGpose) ImGui.EndDisabled();

        // Keybinds tab — configure hotkeys for the morph operations.
        using (var tab = ImRaii.TabItem("Keybinds"))
            if (tab) _keybindsTab.Draw();

        // Easing Curves tab — author/manage custom bezier easing curves, selectable from every
        // EasingMode picker above.
        using (var tab = ImRaii.TabItem("Easing Curves"))
            if (tab) _easingTab.Draw();

        // Render any open file dialog (MCDF origin picker) on top of the window.
        _fileDialog.Draw();
    }

    /// <summary>Pin/Opacity menu opened from the title bar's three-dash button. See the field comment
    /// on <see cref="TitleMenuPopupId"/> for why clickthrough isn't offered here.</summary>
    private void DrawTitleMenu()
    {
        if (!ImGui.BeginPopup(TitleMenuPopupId)) return;

        bool pinned = IsPinned;
        if (ImGui.Checkbox("Pin Window", ref pinned))
            IsPinned = pinned;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Keeps the window from being moved or resized.");

        ImGui.Spacing();
        if (ImGui.Button("Reset"))
            _opacity = 1f;
        ImGui.SameLine();
        float opacityPct = _opacity * 100f;
        ImGui.SetNextItemWidth(160);
        // Floored at 20% rather than 0 — the title bar (and this menu's own trigger) fades with the
        // rest of the window, so a lower floor risks fading the only way back to this control.
        if (ImGui.SliderFloat("Opacity", ref opacityPct, 20f, 100f, "%.0f%%"))
            _opacity = opacityPct / 100f;

        ImGui.EndPopup();
    }
}
