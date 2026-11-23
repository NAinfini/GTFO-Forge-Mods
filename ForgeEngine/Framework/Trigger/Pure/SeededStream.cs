using ForgeEngine.Framework;

namespace ForgeEngine.Trigger.Pure;

// A value-type stream local to one evaluation, never shared or persisted.
internal struct SeededStream
{
    private uint state;
    internal SeededStream(long seed)
    {
        if (seed < 0 || seed > uint.MaxValue)
            throw new RuntimeContractException("pure-seed", "Seed must be an explicit unsigned 32-bit integer.");
        state = (uint)seed;
    }

    /// <summary>The seed of a step the author gave no seed of its own: the step's node, the world, and the trigger
    /// event the step is being evaluated for. Every trigger event draws anew, and every read of the step within one
    /// event — a second consumer, a step resumed after a wait, every peer shown the result — draws the same.</summary>
    internal static long OfEvent(EvaluationContext context)
    {
        // A node id is ASCII (the plan loader's node-id grammar), so its UTF-8 bytes are its characters.
        var hash = ForgeCore.Fnv.MixUtf8(ForgeCore.Fnv.Offset64, context.NodeId);
        if (context.EventId is { } eventId) hash = ForgeCore.Fnv.MixUtf8(hash, eventId);
        // The stream takes an unsigned 32-bit seed: fold the 64-bit hash down so every number is one it accepts.
        return unchecked((uint)(hash ^ (hash >> 32) ^ (ulong)context.Query.WorldEpoch));
    }

    internal double NextUnit()
    {
        unchecked
        {
            state += 0x6d2b79f5u;
            var value = (state ^ (state >> 15)) * (1u | state);
            value ^= value + ((value ^ (value >> 7)) * (61u | value));
            return (value ^ (value >> 14)) / 4294967296d;
        }
    }
}
