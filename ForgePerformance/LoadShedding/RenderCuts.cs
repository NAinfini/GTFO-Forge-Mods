using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace ForgePerformance.LoadShedding;

// Render-pipeline presentation cut that the game's video options do not cover (the options themselves are the player's to change).
// The value is written at level start and put back when the level ends; no hook or per-frame work is added.
internal sealed class RenderCuts
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetBoolCall(bool value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool GetBoolCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong GetCountCall();

    private sealed class Cut
    {
        internal string Name = "";
        internal Func<int> Read = () => 0;
        internal Action<int> Write = _ => { };
        internal Func<bool>? Allowed;
        internal int Target, Original;
        internal bool Enabled, Captured, Changed, Restored;
    }

    // A local comparison tool toggles the cuts through this by reflection, the way it does the IRF flags.
    internal static RenderCuts? Current;

    private readonly LoadSheddingController _owner;
    private readonly List<Cut> _cuts = new();

    internal RenderCuts(LoadSheddingController owner, SheddingSettings settings)
    {
        _owner = owner;
        Add("texture-streaming-off", settings.TextureStreamingOff, 0,
            () => IL2CPP.ResolveICall<GetBoolCall>("UnityEngine.QualitySettings::get_streamingMipmapsActive")() ? 1 : 0,
            value => IL2CPP.ResolveICall<SetBoolCall>("UnityEngine.QualitySettings::set_streamingMipmapsActive")(value != 0),
            // The manager only earns its per-frame renderer walk when a level has textures it can stream.
            () => IL2CPP.ResolveICall<GetCountCall>("UnityEngine.Texture::get_streamingTextureCount")() == 0);
        Current = this;
    }

    private void Add(string name, bool enabled, int target, Func<int> read, Action<int> write, Func<bool>? allowed = null)
        => _cuts.Add(new Cut { Name = name, Enabled = enabled, Target = target, Read = read, Write = write, Allowed = allowed });

    // Each cut is queued as a piece of its own.
    internal void BeginLevel()
    {
        foreach (var cut in _cuts)
        {
            cut.Captured = cut.Changed = cut.Restored = false;
            if (!cut.Enabled) continue;
            var queued = cut;
            _owner.Work.Add("render-" + cut.Name, () => Write(queued, IsAllowed(queued)));
        }
    }

    private bool IsAllowed(Cut cut)
    {
        try { return cut.Allowed == null || cut.Allowed(); }
        catch (Exception error)
        {
            _owner.LogExtraFailure(cut.Name, error.GetType().Name + ": " + error.Message);
            return false;
        }
    }

    internal bool Set(string name, bool on)
    {
        foreach (var cut in _cuts)
            if (cut.Name == name) return Write(cut, on);
        return false;
    }

    private bool Write(Cut cut, bool on)
    {
        try
        {
            var current = cut.Read();
            if (!cut.Captured) { cut.Original = current; cut.Captured = true; }
            var wanted = on ? cut.Target : cut.Original;
            if (current != wanted) { cut.Write(wanted); cut.Changed = true; }
            return true;
        }
        catch (Exception error)
        {
            _owner.LogExtraFailure(cut.Name, error.GetType().Name + ": " + error.Message);
            return false;
        }
    }

    internal void EndLevel()
    {
        foreach (var cut in _cuts)
        {
            if (!cut.Captured) continue;
            // A value the player changed in the level is the player's: only a setting still holding what this cut wrote goes back.
            if (cut.Changed && StillWritten(cut) && Write(cut, false)) cut.Restored = true;
            if (!cut.Enabled) continue;
            _owner.LogItem(cut.Name, 1, cut.Changed ? 1 : 0, cut.Restored ? 1 : 0,
                "original=" + cut.Original.ToString(CultureInfo.InvariantCulture) + "\twritten=" + cut.Target.ToString(CultureInfo.InvariantCulture));
        }
    }

    private bool StillWritten(Cut cut)
    {
        try { return cut.Read() == cut.Target; }
        catch (Exception error)
        {
            _owner.LogExtraFailure(cut.Name, error.GetType().Name + ": " + error.Message);
            return false;
        }
    }

    internal void Dispose() => Current = null;
}
