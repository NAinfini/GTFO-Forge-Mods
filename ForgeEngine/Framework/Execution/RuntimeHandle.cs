using System;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>
/// One handle's identity as a value: the world it was cast in, the pool generation that tells it from every other
/// occupant of its slot, the slot, and the registration index of the provider that cast it — the same four numbers
/// the wire form carries. It compares and hashes by those numbers and allocates nothing, so a provider keys its own
/// tables by it and hands it back to the kernel as it is. The kind and the lifetime are not part of the value: they
/// belong to the port that carries the handle and to the slot the kernel keeps for it.
///
/// The default value names no handle: no cast handle has generation zero.
/// </summary>
public readonly struct RuntimeHandle : IEquatable<RuntimeHandle>
{
    internal RuntimeHandle(int worldEpoch, int lifeEpoch, int local, int provider)
    {
        WorldEpoch = worldEpoch; LifeEpoch = lifeEpoch; Local = local; Provider = provider;
    }

    public int WorldEpoch { get; }
    /// <summary>The pool generation the handle was cast with; zero only for the default value.</summary>
    public int LifeEpoch { get; }
    public int Local { get; }
    public int Provider { get; }

    /// <summary>Whether this is a cast handle's value and not the default.</summary>
    public bool IsCast => LifeEpoch > 0;

    public bool Equals(RuntimeHandle other)
        => WorldEpoch == other.WorldEpoch && LifeEpoch == other.LifeEpoch && Local == other.Local && Provider == other.Provider;
    public override bool Equals(object? obj) => obj is RuntimeHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(WorldEpoch, LifeEpoch, Local, Provider);
    public static bool operator ==(RuntimeHandle left, RuntimeHandle right) => left.Equals(right);
    public static bool operator !=(RuntimeHandle left, RuntimeHandle right) => !left.Equals(right);
    public override string ToString() => WorldEpoch + ":" + LifeEpoch + ":" + Local + ":" + Provider;

    /// <summary>The same slot, life and provider named under another world: what a table that outlived a host
    /// migration re-keys its handles by. The kernel keeps the slot and its generation across the migration, so only
    /// the world differs.</summary>
    public RuntimeHandle InWorld(long worldEpoch) => new(checked((int)worldEpoch), LifeEpoch, Local, Provider);

    /// <summary>The wire form a port, a row target and a network packet carry. It is a document, so it is made where
    /// the value leaves for one of those and not to compare or keep the handle.</summary>
    public JsonElement ToJson() => RuntimeJson.Write(this, static (writer, handle) => handle.Write(writer));

    /// <summary>The wire form, written into a writer the caller owns.</summary>
    internal void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("worldEpoch"u8, WorldEpoch);
        writer.WriteNumber("lifeEpoch"u8, LifeEpoch);
        writer.WriteNumber("local"u8, Local);
        writer.WriteNumber("provider"u8, Provider);
        writer.WriteEndObject();
    }

    /// <summary>Reads a wire-form handle: an object of exactly the four components, each a whole number from 0 to
    /// <see cref="int.MaxValue"/>. Anything else is no handle and answers false.</summary>
    public static bool TryRead(JsonElement value, out RuntimeHandle handle)
    {
        handle = default;
        if (value.ValueKind != JsonValueKind.Object) return false;
        int world = -1, life = -1, local = -1, provider = -1;
        foreach (var field in value.EnumerateObject())
        {
            if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out var component) || component < 0) return false;
            if (field.NameEquals("worldEpoch"u8) && world < 0) world = component;
            else if (field.NameEquals("lifeEpoch"u8) && life < 0) life = component;
            else if (field.NameEquals("local"u8) && local < 0) local = component;
            else if (field.NameEquals("provider"u8) && provider < 0) provider = component;
            else return false;
        }
        if (world < 0 || life < 0 || local < 0 || provider < 0) return false;
        handle = new RuntimeHandle(world, life, local, provider);
        return true;
    }
}

/// <summary>
/// What a handle port declares about the handles it carries: the kind's name in the shared handle-kind table and the
/// lifetime they live for. A module that gives back or cancels one of its own handles names the port it was
/// published through with this, and the kernel checks the handle against exactly these two facts.
/// </summary>
public readonly record struct HandlePort(string HandleKind, string Lifetime);
