using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace BodyFlux.Windows.Tabs;

/// <summary>
/// "Changelog" tab: the full, browsable history from <see cref="ChangelogEntries.All"/>. Independent
/// of the "What's New" modal in <see cref="MainWindow"/> — that's a one-time nudge for unseen
/// versions, this is always the complete log regardless of what's been dismissed.
/// </summary>
public sealed class ChangelogTabView
{
    public void Draw()
    {
        ImGui.Spacing();

        using var scroll = ImRaii.Child("##ChangelogScroll", new System.Numerics.Vector2(0, 0), false);
        if (!scroll) return;

        // A bit larger than the rest of the plugin, this tab is read prose, not controls, so the
        // default UI size felt cramped for it. Reset before returning so nothing else in this
        // window inherits the scale (SetWindowFontScale is per-window state, not auto-scoped).
        ImGui.SetWindowFontScale(1.15f);

        var currentVersion = typeof(Plugin).Assembly.GetName().Version;

        for (int i = 0; i < ChangelogEntries.All.Length; i++)
        {
            var entry = ChangelogEntries.All[i];
            UiHelpers.SectionLabel($"v{entry.Version} — {entry.Title}", null);

            if (entry.Version.Equals(currentVersion))
            {
                ImGui.SameLine();
                UiHelpers.StatusDot(Theme.Success);
                ImGui.SameLine();
                ImGui.TextColored(Theme.Success, "CURRENT");
            }

            ImGui.Spacing();

            // BulletText never wraps regardless of PushTextWrapPos — it always renders as one line
            // and clips. A manual bullet + TextWrapped is the standard workaround.
            foreach (var line in entry.Highlights)
            {
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextWrapped(line);
            }

            if (i < ChangelogEntries.All.Length - 1)
            {
                ImGui.Spacing();
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                ImGui.Spacing();
            }
        }

        ImGui.SetWindowFontScale(1f);
    }
}
