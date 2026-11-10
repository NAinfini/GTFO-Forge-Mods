using System;

namespace ForgePerformance.LoadShedding;

internal enum DecorationClass { Unknown, Gameplay, VisualRotator, CombatDecal, StaticGeneralLight }
internal enum DecorationUse { Shadow, Distance, Animation, Decal, Light }
[Flags]
internal enum DecorationRisk
{
    None = 0, UnknownComponent = 1, Gameplay = 2, Collider = 4, BlocksSight = 8,
    AnimationEvents = 16, RootMotion = 32, StateBehaviour = 64, ControlledLight = 128,
    PlayerLight = 256, UnverifiedLightReceiver = 512
}

// Positive identity comes from exact native types and reviewed native callers,
// never object names, tags, layers, 'static', or absence of a known enemy alone.
internal static class DecorationClassifier
{
    internal const string NonOccludingShaderName = "Legacy Shaders/Particles/Additive";
    internal const string ShaderAssetSha256 = "3BBBDC31F2D9C0A30BD2098A6710606D72708F6F73986D4D10F5A21EE7490CE6";
    internal static bool NonOccludingShader(string name, bool verifiedAsset, bool supported, int passes, int queue)
        => verifiedAsset && supported && passes == 1 && queue >= 3000 && name == NonOccludingShaderName;

    internal static bool Allows(DecorationClass identity, DecorationRisk risks, DecorationUse use)
    {
        if ((risks & (DecorationRisk.UnknownComponent | DecorationRisk.Gameplay | DecorationRisk.Collider
            | DecorationRisk.AnimationEvents | DecorationRisk.RootMotion | DecorationRisk.StateBehaviour)) != 0)
            return false;
        return use switch
        {
            DecorationUse.Shadow => identity == DecorationClass.VisualRotator,
            DecorationUse.Distance => identity == DecorationClass.VisualRotator && (risks & DecorationRisk.BlocksSight) == 0,
            DecorationUse.Animation => identity == DecorationClass.VisualRotator
                && (risks & (DecorationRisk.AnimationEvents | DecorationRisk.RootMotion | DecorationRisk.StateBehaviour)) == 0,
            DecorationUse.Decal => identity == DecorationClass.CombatDecal,
            DecorationUse.Light => identity == DecorationClass.StaticGeneralLight
                && (risks & (DecorationRisk.ControlledLight | DecorationRisk.PlayerLight | DecorationRisk.UnverifiedLightReceiver)) == 0,
            _ => false
        };
    }

    internal static bool IsVisualComponent(string type) => type is
        "UnityEngine.Transform" or "UnityEngine.MeshRenderer" or "UnityEngine.MeshFilter"
        or "UnityEngine.SkinnedMeshRenderer" or "UnityEngine.Animator" or "LG_AnimatorRotator" or "LG_Culler";

    internal static bool IsDecalComponent(string type) => type is
        "UnityEngine.Transform" or "FX_EffectSystem.FX_Decal" or "Decals.Decal";

    internal static bool CombatImpact(int group) => group >= 1 && group <= 16;
}

internal static class DecorationPolicy
{
    internal static float Lifetime(float original, float maximum)
    {
        if (!float.IsFinite(original) || original <= 0) throw new ArgumentOutOfRangeException(nameof(original));
        if (!float.IsFinite(maximum) || maximum <= 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        return Math.Min(original, maximum);
    }
    internal static bool Beyond(float squaredDistance, float limit)
        => float.IsFinite(squaredDistance) && squaredDistance > limit * limit;
    internal static bool KeepLight(bool decorative, ref int count, int limit)
    {
        if (!decorative) return true;
        return count++ < limit;
    }
}

// Fixed storage, original insertion order, and removal before callbacks permit
// native ReturnToPool -> Shown(false) reentrancy without double release.
internal sealed class DecalBudget<T> where T : class
{
    private readonly T?[] _values;
    private readonly float[] _expires;
    internal int Count { get; private set; }
    internal DecalBudget(int capacity)
    {
        if (capacity < 1 || capacity > 4096) throw new ArgumentOutOfRangeException(nameof(capacity));
        _values = new T[capacity]; _expires = new float[capacity];
    }
    internal void Add(T value, float expires, Action<T> retire)
    {
        Remove(value);
        if (Count == _values.Length) Take(0, retire);
        _values[Count] = value; _expires[Count++] = expires;
    }
    internal void Expire(float now, Action<T> retire)
    {
        for (var i = Count - 1; i >= 0; i--) if (now >= _expires[i]) Take(i, retire);
    }
    internal void Remove(T value)
    {
        for (var i = 0; i < Count; i++)
            if (ReferenceEquals(_values[i], value)) { RemoveAt(i); return; }
    }
    internal void Clear() { Array.Clear(_values, 0, Count); Count = 0; }
    private void Take(int index, Action<T> retire)
    { var value = _values[index]!; RemoveAt(index); retire(value); }
    private void RemoveAt(int index)
    {
        Array.Copy(_values, index + 1, _values, index, Count - index - 1);
        Array.Copy(_expires, index + 1, _expires, index, Count - index - 1);
        _values[--Count] = null;
    }
}
