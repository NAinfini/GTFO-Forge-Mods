using System;
using System.IO;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using ForgeAudio;
using ForgeCore;
using ForgeOrdnance;
using GTFO.API;
using HarmonyLib;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnanceArsenal;

[BepInPlugin(Id, "Forge Ordnance Arsenal", Version)]
[BepInDependency("dev.gtfomodding.gtfo-api", ">=0.6.1")]
[BepInDependency("NAinfini.ForgeCore")]
[BepInDependency("NAinfini.ForgeOrdnance")]
[BepInDependency(ForgeUI.UIKit.PluginId)]
[BepInDependency(AudioKit.PluginId)]
[BepInDependency("NAinfini.ForgeVisual")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = "NAinfini.ForgeOrdnanceArsenal";
    internal const string Version = "1.0.0";

    private static readonly ForgeCore.ModLog _log = ForgeCore.Log.For(Id);
    internal static ConfigEntry<bool> ExtraLights = null!;

    /// <summary>Errors are the only log lines. Each exact text is written once.</summary>
    internal static void Error(string message) => _log.ErrorOnce(message);

    /// <summary>A message that has no repeating call site (a rejected network record, whose reason differs each time): written as it is.</summary>
    internal static void Report(string message) => _log.Error(message);

    public override void Load()
    {
        ArsenalKeys.Register();
        ExtraLights = Config.Bind("Presentation", "Extra lights", true, "Point lights on strike areas and beams, and shell impact flashes.");
        // Registration is independent: a refused type must not strand the other registered type without its driver.
        var strike = OrdnanceApi.RegisterPayload(StrikeSpec.TypeName, new StrikePayload());
        var heavy = OrdnanceApi.RegisterPayload(HeavyWeaponSpec.TypeName, new HeavyWeaponPayload());
        if (!strike || !heavy)
            Error("Forge Ordnance refused Arsenal payload type(s): " + (!strike ? StrikeSpec.TypeName + " " : "")
                + (!heavy ? HeavyWeaponSpec.TypeName : "") + "; see its registration error. Accepted types remain available.");
        if (!strike && !heavy)
        {
            return;
        }
        ArsenalNetwork.Register();
        var harmony = new Harmony(Id);
        try
        {
            ArsenalAudio.Register();
            // Before ForgeOrdnance reads the definition files: the turret definitions name looks registered here.
            ArsenalArt.Register();
            // Heavy weapons are named once the definitions are read, so one already in a backpack after a save reload has its name.
            OrdnanceApi.DefinitionsReady += ArsenalText.NameHeavyWeapons;
            harmony.PatchAll(typeof(Plugin).Assembly);
            LauncherFire.Subscribe();
            LauncherBullet.Subscribe();
            LauncherReload.Subscribe();
            ForgeCore.Frame.OnUpdate(Id, Tick);
            ForgeCore.Frame.OnQuit(Id, ArsenalAudio.Dispose);
            LevelAPI.OnBuildDone += ArsenalArt.Load;
            LevelAPI.OnEnterLevel += StrikeBursts.EnterLevel;
            ForgeCore.GameEvents.LevelEnded += () => { StrikeBursts.LeaveLevel(); Void("level cleanup"); };
            EventAPI.OnCheckpointReloaded += () => { StrikeVisuals.CloseReplay(); Void("checkpoint reload"); };
            ForgeCore.GameEvents.MasterChanged += HostChanged;
        }
        catch (Exception error)
        {
            ArsenalAudio.Dispose();
            Error("Forge Ordnance Arsenal initialization failed: " + error);
            harmony.UnpatchSelf();
            throw;
        }
    }

    public override bool Unload() => false;

    /// <summary>Everything in flight is dropped: shells, zones, beams, racks; a lent weapon goes back (except on level exit,
    /// where the game resets the inventory itself).</summary>
    private static void Void(string why)
    {
        try
        {
            ArsenalNetwork.Clear();
            Strikes.Clear();
            StrikeVisuals.Clear();
            HeavyWeapons.Clear(why);
            Grenades.Clear();
            ArsenalTurrets.Clear();
        }
        catch (Exception error) { Error("Clearing the Arsenal state failed (" + why + "): " + error); }
    }

    /// <summary>The host changed on this peer too (a client that stays a client sees the new master as well): everything in flight is void.</summary>
    internal static void HostChanged() => Void("host change");

    internal static void Tick()
    {
        var now = Time.time;
        ArsenalNetwork.Pump();
        if (SNet.IsMaster) Strikes.Tick(now);
        StrikeVisuals.Tick(now);
        HeavyWeapons.Tick(now);
        Grenades.Tick(now);
    }
}
