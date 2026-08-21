using System;

namespace BodyFlux.Windows;

/// <summary>
/// Hand-maintained changelog, newest first. Add one entry here per version bump — same moment as
/// the "Bump to X.Y.Z.W" commit that updates &lt;Version&gt; in BodyFlux.csproj.
/// </summary>
public static class ChangelogEntries
{
    public static readonly ChangelogEntry[] All =
    [
        new ChangelogEntry(new Version(0, 8, 0, 0), "Multi-Target Sync Morphing",
        [
            "Each connected, consenting peer in the Sync tab's grid now has its own morph config " +
                "(destination, speed, mode, easing) and its own Apply, so several peers can be " +
                "set up independently and applied at the same time.",
            "Added Apply All to morph every configured peer at once, plus Pause All, Resume All, " +
                "Reverse All and Reset All to control every active Sync morph together.",
        ]),
        new ChangelogEntry(new Version(0, 7, 2, 0), "Sync Tab Update",
        [
            "Minor UI changes to Sync Tab.",
            "Tooltips added to different options.",
            "Changed Connected Users list to a Grid where you can select the morph target through it. This is the foundation for the multi-target morphing feature coming in next update.",
        ]),
        new ChangelogEntry(new Version(0, 7, 1, 1), "Custom Footer",
        [
            "Added a custom footer, shown on every tab, with the /bodyflux command reminder.",
            "Moved the Customize+ and Brio status indicators from the title bar down to the " +
                "footer, now shown with full text instead of a bare dot.",
        ]),
        new ChangelogEntry(new Version(0, 7, 1, 0), "Added Changelog Info",
        [
            "Added Changelog Window on first open after new version and Changelog tab to review the history of previous versions.",
            "Fixed some minor UI artifacts.",
        ]),
        new ChangelogEntry(new Version(0, 7, 0, 0), "Custom Easing Curves",
        [
            "Author your own bezier easing curves in the new Easing Curves tab, with a live " +
                "draggable editor and instant preview.",
            "Custom curves are selectable everywhere easing was already offered — Player, Gpose, " +
                "Sequences, and Gauge Bind.",
            "Consistent padding between the window edge and every tab's content.",
        ]),
        new ChangelogEntry(new Version(0, 6, 0, 0), "New Custom UI",
        [
            "Reworked look across the whole plugin: a custom title bar and a refreshed color theme for a custom appearance.",
        ]),
        new ChangelogEntry(new Version(0, 5, 9, 2), "Fix Commands",
        [
            "Fixed plugin commands not working properly before first window open.",
        ]),
        new ChangelogEntry(new Version(0, 5, 9, 1), "Sync Fix",
        [
            "Quick fix for syncing issues.",
        ]),
        new ChangelogEntry(new Version(0, 5, 9, 0), "Gauge Bind Feature",
        [
            "Added Basic Gauge Bind — morph tracks a live linked resource like MP or a job gauge.",
        ]),
        new ChangelogEntry(new Version(0, 5, 8, 5), "Preset Origin Fix",
        [
            "Fixed presets not starting from the proper origin profile.",
        ]),
        new ChangelogEntry(new Version(0, 5, 8, 4), "Origin Profile & Overlay Targeting",
        [
            "Added the /bodyflux reset [origin/active] chat command.",
            "Fixed independent child bone scaling.",
            "Added the ability to select both an Origin Profile and a Morph Target " +
                "(Full Profile / Template Overlay).",
        ]),
        new ChangelogEntry(new Version(0, 5, 8, 2), "Child Bone Rotation Fix",
        [
            "Fixed rotation and translation not applying to child bones in a morph.",
        ]),
        new ChangelogEntry(new Version(0, 5, 8, 1), "Template Overlay Bone Linking Fix",
        [
            "Fixed bone linking when applying a Template Overlay.",
        ]),
        new ChangelogEntry(new Version(0, 5, 8, 0), "Origin Profile Selection",
        [
            "Added a selectable Origin Profile.",
            "Added separate Reset to Origin and Reset to Active buttons.",
        ]),
        new ChangelogEntry(new Version(0, 5, 7, 0), "Morph Target Modes",
        [
            "Added Morph Target Modes: Full Profile and Template Overlay.",
        ]),
        new ChangelogEntry(new Version(0, 5, 6, 4), "Sync Fixes & Scrollable Tabs",
        [
            "Added scroll bars to every tab.",
            "Fixed a synced peer not resetting correctly.",
        ]),
        new ChangelogEntry(new Version(0, 5, 6, 3), "Sync Infrastructure Fixes",
        [
            "Fixes to the Sync infrastructure.",
            "Fixed the morph confirmation window.",
            "Increased the internal daily Sync quota limit.",
        ]),
        new ChangelogEntry(new Version(0, 5, 6, 2), "Sync Overhaul & Configurable Keybinds",
        [
            "Added configurable keybinds.",
            "Added a confirmation window for incoming Sync requests.",
            "Increased preset slots to 50.",
            "Fixed the peer list and linked-children bone morphing.",
            "Clearer growth speed sliders; added a reset button to sequences.",
            "Refactored the Sync/relay architecture for smoother peer animation.",
            "Added scroll bars to the preset list and keybinds.",
        ]),
        new ChangelogEntry(new Version(0, 5, 3, 1), "Sync Privacy & Relay Refactor",
        [
            "Relay refactor with added privacy for the Sync feature.",
            "Pruned empty relay rooms; optimized server load.",
            "Fixed the plugin icon not showing in the Dalamud installer.",
        ]),
    ];
}
