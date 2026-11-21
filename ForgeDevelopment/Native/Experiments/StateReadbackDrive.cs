using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Gear;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeDevelopment.Native;

// Development-only readbacks (and the three stamina requests F3 needs) for the local player, so a driver session can
// check a card's native effect from outside the mod under test. Each method returns one `name=value; ...` line the
// experiment engine records; a missing owner, backpack or weapon is an explicit `unavailable:<why>`, never a zero.
internal static class StateReadbackDrive
{
    private static string Number(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static bool Local(out PlayerAgent player, out string why)
    {
        player = ForgeCore.Players.Local!;
        why = player == null || player.Pointer == IntPtr.Zero ? "no-local-player" : "";
        return why.Length == 0;
    }

    private static bool Stamina(out PlayerStamina stamina, out string why)
    {
        stamina = null!;
        if (!Local(out var player, out why)) return false;
        stamina = player.Stamina;
        why = stamina == null || stamina.Pointer == IntPtr.Zero ? "no-stamina" : "";
        return why.Length == 0;
    }

    private static string StaminaLine(PlayerStamina stamina)
        => "stamina=" + Number(stamina.Stamina) + "; relative=" + Number(stamina.StaminaRelative) + "; max=" + Number(PlayerStamina.MaxStamina);

    /// <summary>The local player's reserve, its 0..1 ratio and the game's ceiling.</summary>
    internal static string StaminaRead() => Stamina(out var stamina, out var why) ? StaminaLine(stamina) : "unavailable:" + why;

    /// <summary>One spend through the game's own `UseStamina` (full cost both in and out of combat, one second), then the reserve.</summary>
    internal static string StaminaUse(float cost)
    {
        if (!Stamina(out var stamina, out var why)) return "unavailable:" + why;
        var before = stamina.Stamina;
        stamina.UseStamina(new PlayerStamina.ActionCost
        {
            baseStaminaCostInCombat = cost,
            baseStaminaCostOutOfCombat = cost,
            resetRestingTimerInCombat = true,
            resetRestingTimerOutOfCombat = true
        }, 1f);
        return "before=" + Number(before) + "; " + StaminaLine(stamina);
    }

    /// <summary>The game's own `ResetStamina` (fills the reserve), then the reserve.</summary>
    internal static string StaminaReset()
    {
        if (!Stamina(out var stamina, out var why)) return "unavailable:" + why;
        var before = stamina.Stamina;
        stamina.ResetStamina();
        return "before=" + Number(before) + "; " + StaminaLine(stamina);
    }

    /// <summary>The one direct write: the reserve is set to `value` held under `cap`, as a capped stamina change does.</summary>
    internal static string StaminaWrite(float value, float cap)
    {
        if (!Stamina(out var stamina, out var why)) return "unavailable:" + why;
        var before = stamina.Stamina;
        stamina.Stamina = Math.Min(value, cap);
        return "before=" + Number(before) + "; " + StaminaLine(stamina);
    }

    /// <summary>The wielded item's magazine (a gun's clip) and the player's five backpack ammo pools, for fire, throw and ammo-change
    /// checks. Any wielded item reads: a held consumable's remaining uses are the <c>consumable</c> pool.</summary>
    internal static string AmmoRead()
    {
        if (!Local(out var player, out var why)) return "unavailable:" + why;
        var weapon = player.Inventory?.WieldedItem ?? player.FPItemHolder?.WieldedItem;
        if (weapon == null || weapon.WasCollected) return "unavailable:no-wielded-item";
        if (player.Owner == null || !PlayerBackpackManager.TryGetBackpack(player.Owner, out var backpack) || backpack?.AmmoStorage == null)
            return "unavailable:no-backpack";
        var storage = backpack.AmmoStorage;
        return "clip=" + weapon.GetCurrentClip() + "; maxClip=" + weapon.GetMaxClip() + "; standard=" + Pool(storage.StandardAmmo)
            + "; special=" + Pool(storage.SpecialAmmo) + "; class=" + Pool(storage.ClassAmmo)
            + "; resourcePack=" + Pool(storage.ResourcePackAmmo) + "; consumable=" + Pool(storage.ConsumableAmmo);
    }

    /// <summary>The clip_set card's own native entry, `BulletWeapon.SetCurrentClip` on the wielded gun, then the magazine.</summary>
    internal static string ClipSet(int clip)
    {
        if (!Local(out var player, out var why)) return "unavailable:" + why;
        var weapon = player.Inventory?.WieldedItem?.TryCast<BulletWeapon>() ?? player.FPItemHolder?.WieldedItem?.TryCast<BulletWeapon>();
        if (weapon == null || weapon.WasCollected) return "unavailable:no-wielded-gun";
        var before = weapon.GetCurrentClip();
        weapon.SetCurrentClip(clip);
        return "before=" + before + "; clip=" + weapon.GetCurrentClip() + "; maxClip=" + weapon.GetMaxClip();
    }

    private static string Pool(InventorySlotAmmo? ammo) => ammo == null ? "-" : Number(ammo.AmmoInPack);

    /// <summary>The local SNet player's lookup (non-zero offline in single player) and the master's.</summary>
    internal static string NetLookup()
    {
        var local = SNet.LocalPlayer;
        if (local == null) return "unavailable:no-local-snet-player";
        return "local=" + local.Lookup.ToString(CultureInfo.InvariantCulture) + "; master=" + (SNet.Master?.Lookup ?? 0).ToString(CultureInfo.InvariantCulture)
            + "; isMaster=" + SNet.IsMaster;
    }

    /// <summary>The warden-intel HUD widget's state: whether its objective sequence runs, the queued sub-objective messages and its text.</summary>
    internal static string IntelRead()
    {
        var intel = GuiManager.PlayerLayer?.m_wardenIntel;
        if (intel == null || intel.Pointer == IntPtr.Zero) return "unavailable:no-warden-intel";
        return "sequenceRunning=" + intel.IsNewObjectiveSequenceRunning + "; text=" + (intel.m_intelText?.text ?? "");
    }

    /// <summary>What the HUD shows now, read from the game's own widgets and only read: the objective panel (<c>PUI_GameObjectives</c>:
    /// header, the main objective text, each progression objective's header and text), the warden intel line, the objective timer (title
    /// and time), the interaction prompt and the centre message (<c>InteractionGuiLayer</c>, with whether each is visible), and the scan
    /// rings' world text (<c>CP_Bioscan_Hud</c>, the visible ones, the one nearest the player first). Each text has its rich-text tags
    /// removed, line breaks as spaces and is cut at <see cref="HudText"/> characters. Not readable here: the subtitles and voice lines,
    /// the compass and radar, a nav marker's on-screen text, the player's own status bars as text (their numbers are AmmoRead and the
    /// player reads).</summary>
    internal static string HudRead()
    {
        var player = GuiManager.PlayerLayer;
        var interaction = GuiManager.InteractionLayer;
        if (player == null || player.Pointer == IntPtr.Zero || interaction == null || interaction.Pointer == IntPtr.Zero) return "unavailable:no-hud";
        var objectives = player.WardenObjectives;
        var parts = new List<string> { "objectiveHeader=" + Hud(objectives?.m_header), "objective=" + Hud(objectives?.m_items) };
        var progression = objectives?.m_progressionObjectives;
        if (progression != null)
            for (var i = 0; i < progression.Count && i < HudRows; i++)
                parts.Add("progression[" + i + "]=" + Hud(progression[i]?.m_header) + " : " + Hud(progression[i]?.m_text));
        parts.Add("intel=" + Hud(player.m_wardenIntel?.m_intelText));
        var timer = player.m_objectiveTimer;
        parts.Add("timerVisible=" + (timer != null && timer.gameObject.activeInHierarchy) + "; timerTitle=" + Hud(timer?.m_titleText) + "; timer=" + Hud(timer?.m_timerText));
        parts.Add("promptVisible=" + interaction.InteractPromptVisible + "; prompt=" + Hud(interaction.m_interactPrompt?.m_headerText));
        parts.Add("messageVisible=" + interaction.MessageVisible + "; message=" + Hud(interaction.m_message?.m_headerText));
        var scans = UnityEngine.Object.FindObjectsOfType<ChainedPuzzles.CP_Bioscan_Hud>()
            .Where(hud => hud != null && !hud.WasCollected && hud.m_visible)
            .OrderByDescending(hud => hud.m_isClosestToPlayer).Take(HudRows).ToList();
        parts.Add("scans=" + scans.Count);
        for (var i = 0; i < scans.Count; i++) parts.Add("scan[" + i + "]=" + Hud(scans[i].m_bioscanWorldText));
        return string.Join("; ", parts);
    }

    private const int HudText = 160, HudRows = 6;

    private static string Hud(TMPro.TextMeshPro? text)
    {
        if (text == null || text.WasCollected) return "";
        var clean = System.Text.RegularExpressions.Regex.Replace(text.text ?? "", "<[^>]*>", "").Replace('\n', ' ').Replace('\r', ' ').Replace(';', ',').Trim();
        return clean.Length <= HudText ? clean : clean.Substring(0, HudText);
    }

    /// <summary>The test action for a fog change: sets Unity's scene fog density and answers the value before and after.</summary>
    internal static string FogSet(float density)
    {
        var before = RenderSettings.fogDensity;
        RenderSettings.fogDensity = density;
        return "before=" + Number(before) + "; after=" + Number(RenderSettings.fogDensity);
    }

    /// <summary>Unity's ambient light strength and fog colour alpha, the scene-level values a fog or environment change lands on.</summary>
    internal static string FogRead()
        => "ambientIntensity=" + Number(RenderSettings.ambientIntensity) + "; fogEnabled=" + RenderSettings.fog
            + "; fogDensity=" + Number(RenderSettings.fogDensity) + "; fogColorAlpha=" + Number(RenderSettings.fogColor.a);
}
