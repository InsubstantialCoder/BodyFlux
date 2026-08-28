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
        new ChangelogEntry(new Version(0, 8, 3, 0), "Multi-Point Easing Curves",
        [
            "Custom curves in the Easing Curves tab are no longer limited to two control handles " +
                "and can now have up to 8 points, for shapes that were simply impossible " +
                "before, like a pause in the middle of a morph or a bounce that settles.",
            "The points now sit directly on the curve instead of pulling at it from outside, so " +
                "where you put a point is where the morph actually passes through. Double-click " +
                "the graph to add one, drag it to move it, right-click it to delete it, and select " +
                "it to bend the curve either side of it with its two handles.",
            "Adding a point never changes the shape you already have. The curve stays exactly as " +
                "it was and you get a new handle to bend it from.",
            "Added new multi-point presets to start from: Bounce (overshoots and rings down into " +
                "place), Anticipate (dips the other way first, then swings through), Hold (a dead " +
                "stop halfway through the morph) and Steps (arrives in three visible stages).",
            "Every curve you have already saved keeps working exactly as before and looks " +
                "identical, nothing needs re-making, and old curves can be extended with extra " +
                "points whenever you feel like it.",
            "Custom curves apply to Sync morphs too: a morph you drive on someone else's " +
                "character follows your curve on their screen, multi-point shapes included.",
        ]),
        new ChangelogEntry(new Version(0, 8, 2, 0), "Allow Morph Requests Rework",
        [
            "\"Allow others to morph my character\" is now a 3-way \"Allow Morph Requests\" " +
                "choice: Off (unreachable, as before), Ask (the default, every request still " +
                "needs your explicit Accept/Deny, unchanged from before), or Auto (requests from " +
                "anyone with your Sync Key are granted instantly, no prompt).",
            "The Sync tab's Connected Users grid now always shows every current member of your " +
                "Sync Key, not just those who accept requests. A peer with Requests set to Off " +
                "just has their Request/Apply button disabled with a tooltip explaining why, " +
                "instead of disappearing from the grid.",
        ]),
        new ChangelogEntry(new Version(0, 8, 1, 0), "Faster Morphs & Layout Refresh",
        [
            "Morph speed sliders (Single, Gpose, Sync, Sequences) now go well past the old " +
                "1-second floor, down to 0.10s, and Sequences no longer clamps a typed value back " +
                "below 1s the way it used to; Single already allowed this by typing manually, it's " +
                "now consistent everywhere.",
            "Speed sliders are driven directly by \"Morph Time\" in seconds instead of an internal " +
                "range/sec figure, with a logarithmic curve for finer control at the low end. " +
                "Ctrl+Click the slider to type an exact value.",
            "Fixed the slider's value text getting covered by its own grab dot, and the grab being " +
                "clipped in half at the very ends of the track.",
            "Renamed \"Growth Speed\"/\"Speed\" to \"Morph Time\" and \"Mode\" to \"Morph Loop\" " +
                "across every tab.",
            "Reworked the Gpose Multi tab to use the same card-based 2-column grid as the Sync tab, " +
                "instead of a single stacked column of collapsible headers, with more breathing room " +
                "between controls (also applied to Sync's peer cards).",
        ]),
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
