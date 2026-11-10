using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgePerformance.LoadShedding;

internal sealed class VisorLiquidSuppression
{
    // GlassLiquidSystem.Setup binds rtParams and rtColor to the UI blitter instance under exactly these two names
    // (RVA 0x13B7288, 0x13B72A7); nothing else on the HUD material is touched. The shader does not list them as declared
    // properties, so they cannot be discovered from the material.
    private const string ParamsSlot = "_GlassLiquids_Params", ColourSlot = "_GlassLiquids_Colour";
    private readonly Dictionary<IntPtr, MaterialState> _materials = new();
    private readonly Action<string, bool> _refuse;
    private Texture2D? _empty;
    internal long Seen, Changed, Restored, SkippedCommands;
    internal int Refusals { get; private set; }
    // A binding this build lacks refuses the cut for the session; a level whose liquid system was not ready refuses that level only.
    private bool _sessionRefused, _levelRefused;
    internal bool Refused => _sessionRefused || _levelRefused;
    internal VisorLiquidSuppression(Action<string, bool> refuse) => _refuse = refuse;
    internal void BeginLevel() { Seen = Changed = Restored = SkippedCommands = 0; _levelRefused = false; }

    internal void Suppress(GlassLiquidSystem system)
    {
        if (Refused) return;
        try { Bind(system); }
        catch (Exception error) { Refuse(error); }
    }
    private void Bind(GlassLiquidSystem system)
    {
        if (system == null || system.HUDMaterial == null)
            throw new InvalidOperationException("The game's screen liquid system has no HUD material.");
        var material = system.HUDMaterial;
        if (_materials.ContainsKey(material.Pointer)) return;
        if (system.rtParams == null || system.rtColor == null || system.rtParams == system.rtColor)
            throw new InvalidOperationException("The two texture bindings of the visor liquid material are unavailable.");
        if (_empty == null)
        {
            _empty = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _empty.SetPixel(0, 0, Color.clear);
            _empty.Apply(false, true);
        }
        var state = new MaterialState(material, new TextureSlot[] { new(ParamsSlot, system.rtParams), new(ColourSlot, system.rtColor) });
        _materials.Add(material.Pointer, state);
        Seen++;
        foreach (var slot in state.Slots) material.SetTexture(slot.Name, _empty);
        Changed++;
    }

    // Resolution changes recreate RTs and rebind their slots. Release our old
    // bindings BEFORE vanilla Setup, then capture the new pair in its postfix.
    internal void BeforeSetup(GlassLiquidSystem system)
    {
        if (Refused) return;
        try
        {
            if (system == null || system.HUDMaterial == null) return;
            if (_materials.TryGetValue(system.HUDMaterial.Pointer, out var state))
            {
                Restore(state);
                _materials.Remove(system.HUDMaterial.Pointer);
            }
        }
        catch (Exception error) { Refuse(error); }
    }
    // Binding is proven only at level scan / Setup. Never inspect an IL2CPP
    // object, look up a material or retry a refused binding on the frame path.
    internal bool Collect()
    {
        if (Refused) return true;
        SkippedCommands++;
        return false;
    }
    internal void Refuse(Exception error)
    {
        if (Refused) return;
        Refusals++;
        var session = error is MissingMemberException or TypeLoadException or EntryPointNotFoundException;
        if (session) _sessionRefused = true; else _levelRefused = true;
        var reason = error.GetType().Name + ": " + error.Message;
        try { EndLevel(); }
        catch (Exception restoration) { reason += "; restore failed: " + restoration.GetType().Name + ": " + restoration.Message; }
        // Installed hooks stay in place; the refused latch lets vanilla run.
        // A reporting failure must not escape a native hook.
        try { _refuse(reason, session); }
        catch (Exception reporting)
        { Console.Error.WriteLine("visor-liquid-off refusal report failed: " + reason + "; " + reporting.GetType().Name + ": " + reporting.Message); }
    }
    private void Restore(MaterialState state)
    {
        if (state.Material == null) return;
        foreach (var slot in state.Slots) state.Material.SetTexture(slot.Name, slot.Original);
        Restored++;
    }
    internal void EndLevel()
    {
        Exception? first = null;
        foreach (var state in _materials.Values)
            try { Restore(state); } catch (Exception error) { first ??= error; }
        _materials.Clear();
        if (_empty != null) UnityEngine.Object.Destroy(_empty);
        _empty = null;
        if (first != null) throw first;
    }
    private sealed record TextureSlot(string Name, Texture Original);
    private sealed record MaterialState(Material Material, TextureSlot[] Slots);
}
