using System;
using System.Collections.Generic;

namespace ForgeOrdnance;

/// <summary>Runs one ForgeCore StrictJson read for a data file that collects every refusal: the refusal is added to <c>errors</c> by entry and field and the read returns <c>failed</c>.</summary>
internal static class StrictRead
{
    internal static T Collect<T>(Func<T> read, string at, List<string> errors, T failed)
    {
        try { return read(); }
        catch (ForgeCore.StrictJsonException error) { errors.Add(at + " " + error.Field + ": " + error.Message); return failed; }
    }
}
