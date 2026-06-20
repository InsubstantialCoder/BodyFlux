using Dalamud.Game.ClientState.JobGauge.Types;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;

namespace BodyFlux.Morph;

/// <summary>
/// Bar-type resources a gauge-linked morph can follow. Only continuous 0–100 gauges
/// (plus MP) are included — discrete stack gauges (Chakra, Feathers, Ammo, …) are
/// intentionally excluded because they read as stepped, not smooth.
/// </summary>
public enum GaugeResource
{
    Mp,                  // universal
    PldOath,             // PLD  OathGauge        0–100
    WarBeast,            // WAR  BeastGauge       0–100
    DrkBlood,            // DRK  Blood            0–100
    SchFaerie,           // SCH  FairyGauge       0–100
    NinNinki,            // NIN  Ninki            0–100
    SamKenki,            // SAM  Kenki            0–100
    RprSoul,             // RPR  Soul             0–100
    RprShroud,           // RPR  Shroud           0–100
    VprSerpentOffering,  // VPR  SerpentOffering  0–100
    BrdSoulVoice,        // BRD  SoulVoice        0–100
    MchHeat,             // MCH  Heat             0–100
    MchBattery,          // MCH  Battery          0–100
    DncEspirit,          // DNC  Esprit           0–100
    RdmWhiteMana,        // RDM  WhiteMana        0–100
    RdmBlackMana,        // RDM  BlackMana        0–100
    PctPalette,          // PCT  PalleteGauge     0–100
}

/// <summary>
/// Reads the live fill (0–1) of a <see cref="GaugeResource"/> for the local player.
/// Self / local player only — gauge memory is not exposed for other actors, so gauge
/// morphs are a local-player feature (see "Constraints").
/// </summary>
public sealed class GaugeReader(IJobGauges gauges, IObjectTable objects)
{
    /// <summary>
    /// Live 0–1 fill for the resource, or <c>null</c> when it can't be read
    /// (logged out, or not on the matching job). <c>null</c> means: hold the
    /// current morph where it is, don't drive it.
    /// </summary>
    public float? Read(GaugeResource r)
    {
        // Index 0 is the local player; matches the existing codebase idiom for reading self.
        if (objects[0] is not IPlayerCharacter player) return null;

        // MP is universal — no job guard.
        if (r == GaugeResource.Mp)
            return player.MaxMp == 0 ? null : (float)player.CurrentMp / player.MaxMp;

        uint job = player.ClassJob.RowId;

        return r switch
        {
            GaugeResource.PldOath            when job == 19 => gauges.Get<PLDGauge>().OathGauge      / 100f,
            GaugeResource.WarBeast           when job == 21 => gauges.Get<WARGauge>().BeastGauge     / 100f,
            GaugeResource.DrkBlood           when job == 32 => gauges.Get<DRKGauge>().Blood          / 100f,
            GaugeResource.SchFaerie          when job == 28 => gauges.Get<SCHGauge>().FairyGauge     / 100f,
            GaugeResource.NinNinki           when job == 30 => gauges.Get<NINGauge>().Ninki          / 100f,
            GaugeResource.SamKenki           when job == 34 => gauges.Get<SAMGauge>().Kenki          / 100f,
            GaugeResource.RprSoul            when job == 39 => gauges.Get<RPRGauge>().Soul           / 100f,
            GaugeResource.RprShroud          when job == 39 => gauges.Get<RPRGauge>().Shroud         / 100f,
            GaugeResource.VprSerpentOffering when job == 41 => gauges.Get<VPRGauge>().SerpentOffering / 100f,
            GaugeResource.BrdSoulVoice       when job == 23 => gauges.Get<BRDGauge>().SoulVoice      / 100f,
            GaugeResource.MchHeat            when job == 31 => gauges.Get<MCHGauge>().Heat           / 100f,
            GaugeResource.MchBattery         when job == 31 => gauges.Get<MCHGauge>().Battery        / 100f,
            GaugeResource.DncEspirit         when job == 38 => gauges.Get<DNCGauge>().Esprit         / 100f,
            GaugeResource.RdmWhiteMana       when job == 35 => gauges.Get<RDMGauge>().WhiteMana      / 100f,
            GaugeResource.RdmBlackMana       when job == 35 => gauges.Get<RDMGauge>().BlackMana      / 100f,
            GaugeResource.PctPalette         when job == 42 => gauges.Get<PCTGauge>().PalleteGauge   / 100f,
            _ => null,
        };
    }

    /// <summary>Human-readable label for UI dropdowns.</summary>
    public static string Label(GaugeResource r) => r switch
    {
        GaugeResource.Mp                 => "MP",
        GaugeResource.PldOath            => "PLD — Oath Gauge",
        GaugeResource.WarBeast           => "WAR — Beast Gauge",
        GaugeResource.DrkBlood           => "DRK — Blood Gauge",
        GaugeResource.SchFaerie          => "SCH — Fae Aether",
        GaugeResource.NinNinki           => "NIN — Ninki",
        GaugeResource.SamKenki           => "SAM — Kenki",
        GaugeResource.RprSoul            => "RPR — Soul Gauge",
        GaugeResource.RprShroud          => "RPR — Shroud Gauge",
        GaugeResource.VprSerpentOffering => "VPR — Serpent Offering",
        GaugeResource.BrdSoulVoice       => "BRD — Soul Voice",
        GaugeResource.MchHeat            => "MCH — Heat Gauge",
        GaugeResource.MchBattery         => "MCH — Battery Gauge",
        GaugeResource.DncEspirit         => "DNC — Esprit",
        GaugeResource.RdmWhiteMana       => "RDM — White Mana",
        GaugeResource.RdmBlackMana       => "RDM — Black Mana",
        GaugeResource.PctPalette         => "PCT — Palette Gauge",
        _ => r.ToString(),
    };
}
