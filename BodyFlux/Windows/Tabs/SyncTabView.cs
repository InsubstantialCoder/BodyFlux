using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace BodyFlux.Windows.Tabs;

/// <summary>
/// Renders the "Sync" tab: the group Sync Key, the remote-morph consent toggle,
/// connection controls, status, and the list of connected peers.
/// </summary>
public sealed class SyncTabView
{
    private readonly Plugin plugin;

    public SyncTabView(Plugin plugin) => this.plugin = plugin;

    private static string GeneratePairKey()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var buf = new char[9];
        for (int i = 0; i < 4; i++) buf[i] = chars[Random.Shared.Next(chars.Length)];
        buf[4] = '-';
        for (int i = 5; i < 9; i++) buf[i] = chars[Random.Shared.Next(chars.Length)];
        return new string(buf);
    }

    public void Draw()
    {
        using var scroll = ImRaii.Child("##SyncScroll", new Vector2(0, 0), false);
        if (!scroll) return;

        float bw     = 90 * ImGuiHelpers.GlobalScale;
        float labelW = 90 * ImGuiHelpers.GlobalScale;
        var   config = plugin.Configuration;

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            "Generate a Sync Key and share it with your partner(s) to sync together " +
            "and see each other's morphs in real time.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Enable toggle ─────────────────────────────────────────────────────
        bool syncEnabled = config.NetworkSyncEnabled;
        if (ImGui.Checkbox("Enable Group Sync", ref syncEnabled))
        {
            config.NetworkSyncEnabled = syncEnabled;
            config.Save();
            if (!syncEnabled)
                plugin.DisconnectNetworkSync();
        }

        ImGui.Spacing();

        bool allowRemote = plugin.AllowRemoteMorph;
        if (ImGui.Checkbox("Allow others to morph my character", ref allowRemote))
            plugin.AllowRemoteMorph = allowRemote;

        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled("When enabled, connected players with your Sync Key can apply morph profiles to your character.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();

        // ── Sync Key row ──────────────────────────────────────────────────────
        // Fixed width instead of stretching to fill the row: the key is always "XXXX-XXXX" (9
        // chars), so a wide field just pushes Generate/Copy/Connect toward the right edge for no
        // reason — this keeps them right after the field instead.
        float keyFieldW = 120 * ImGuiHelpers.GlobalScale;

        bool isActive = plugin.IsNetworkActive;

        UiHelpers.SectionLabel("Sync Key:", null);
        ImGui.SameLine(labelW);
        if (isActive) ImGui.BeginDisabled();
        ImGui.SetNextItemWidth(keyFieldW);
        string pairKey = config.PairKey;
        if (ImGui.InputText("##SyncKey", ref pairKey, 64))
        {
            config.PairKey = pairKey;
            config.Save();
        }

        ImGui.SameLine();
        if (UiHelpers.PrimaryButton("Generate", new Vector2(bw, 0)))
        {
            config.PairKey = GeneratePairKey();
            config.Save();
        }
        if (isActive) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##copySyncKey", FontAwesomeIcon.Copy))
            ImGui.SetClipboardText(config.PairKey);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy Sync Key to clipboard");

        ImGui.SameLine();
        if (plugin.IsNetworkActive)
        {
            if (UiHelpers.DangerButton("Disconnect", new Vector2(bw, 0)))
                plugin.DisconnectNetworkSync();
        }
        else if (plugin.IsNetworkIdleDisconnected || plugin.IsNetworkQuotaExhausted)
        {
            if (UiHelpers.PrimaryButton("Reconnect", new Vector2(bw, 0)))
                plugin.ApplyNetworkSyncConfig();
        }
        else
        {
            if (UiHelpers.PrimaryButton("Connect", new Vector2(bw, 0)))
                plugin.ApplyNetworkSyncConfig();
        }

        // ── Connection status ─────────────────────────────────────────────────
        ImGui.Spacing();
        var status = plugin.NetworkSyncStatus;
        var statusColor = status == "Connected"
            ? new Vector4(0.3f, 0.9f, 0.4f, 1f)
            : status.StartsWith("Connecting")
                ? new Vector4(0.9f, 0.8f, 0.2f, 1f)
                : status is "Disconnected (idle)" or "Daily quota exceeded"
                    ? new Vector4(0.9f, 0.6f, 0.2f, 1f)  // amber — auto-disconnected
                    : new Vector4(0.7f, 0.7f, 0.7f, 1f);
        ImGui.TextColored(statusColor, $"Status: {status}");

        // ── Daily quota exhausted notice ──────────────────────────────────────
        if (plugin.IsNetworkQuotaExhausted)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(new Vector4(0.9f, 0.4f, 0.2f, 1f),
                "Daily relay quota exceeded. Sync is paused — please try again tomorrow.");
            ImGui.PopTextWrapPos();
        }

        // ── Lone-connection auto-disconnect notice ────────────────────────────
        // The relay only needs to carry frames between connected players, so a lone connection is
        // auto-disconnected to save bandwidth. Show the running countdown while alone.
        if (plugin.IsNetworkAwaitingIdleDisconnect)
        {
            int secs = plugin.NetworkIdleDisconnectSecondsRemaining;
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(new Vector4(0.9f, 0.6f, 0.2f, 1f),
                $"No peers connected to Sync Key... Auto-disconnecting in {secs / 60}:{secs % 60:D2}");
            ImGui.PopTextWrapPos();
        }

        // ── Connected users ───────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        var maxPeers = plugin.NetworkMaxPeers;
        if (maxPeers.HasValue)
            ImGui.TextDisabled($"Up to {maxPeers} players can share the same Sync Key simultaneously.");
        UiHelpers.SectionLabel("Connected users:", null);

        var peers     = plugin.ConnectedPeers;
        var localName = plugin.LocalPlayerName;

        if (peers == null)
        {
            ImGui.TextDisabled("  (not connected)");
        }
        else
        {
            if (!string.IsNullOrEmpty(localName))
                ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.6f, 1f), $"  {localName} (you)");

            // peers is keyed by display label (real name for nearby peers, short opaque id for
            // distant ones — names never cross the relay). Show everyone seen within the stale window.
            var  cutoff  = DateTime.UtcNow.AddSeconds(-90);
            bool anyPeer = false;
            foreach (var kv in peers)
            {
                if (kv.Value < cutoff) continue;
                ImGui.TextUnformatted($"  {kv.Key}");
                anyPeer = true;
            }
            if (!anyPeer)
                ImGui.TextDisabled("  No other users connected.");
        }
    }
}
