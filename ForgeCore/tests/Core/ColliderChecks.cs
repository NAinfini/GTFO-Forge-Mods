using UnityEngine;

namespace ForgeCore.Tests;

/// <summary>The collider-level sphere query: results and counts, the layer mask and trigger mode passed through, a full buffer
/// reported as truncated (never silently complete), and no stale colliders after a shorter query.</summary>
internal static class ColliderChecks
{
    internal static void Run(Action<bool, string> check)
    {
        static Collider Box() => new() { gameObject = new() };
        var a = Box(); var b = Box(); var c = Box();
        var query = new ColliderQuery(3);

        Physics.Hits = new[] { a, b };
        var count = query.Sphere(default, 2f, 0x1234, QueryTriggerInteraction.Ignore);
        check(count == 2 && query.Count == 2 && query[0] == a && query[1] == b, "colliders: the query returns the colliders in range");
        check(!query.Truncated, "colliders: a partly filled buffer is complete");
        check(Physics.Last.Mask == 0x1234 && Physics.Last.Trigger == QueryTriggerInteraction.Ignore, "colliders: the layer mask and trigger mode reach the game query");

        Physics.Hits = new[] { a, b, c };
        query.Sphere(default, 2f, 1, QueryTriggerInteraction.UseGlobal);
        check(query.Truncated && query.Count == 3, "colliders: a full buffer is reported as truncated");
        Physics.Hits = new[] { a, b, c, a, b };
        query.Sphere(default, 2f, 1, QueryTriggerInteraction.UseGlobal);
        check(query.Truncated && query.Count == 3 && query[2] == c, "colliders: more hits than capacity keep the first ones and report truncation");

        Physics.Hits = new[] { b };
        query.Sphere(default, 2f, 1, QueryTriggerInteraction.Collide);
        check(!query.Truncated && query.Count == 1 && query[0] == b, "colliders: a later, smaller query clears the truncation");
        var stale = false;
        try { _ = query[1]; } catch (ArgumentOutOfRangeException) { stale = true; }
        check(stale, "colliders: results beyond the last query's count are not readable");

        Physics.Hits = Array.Empty<Collider>();
        check(query.Sphere(default, 2f, 1, QueryTriggerInteraction.Ignore) == 0 && query.Count == 0 && !query.Truncated, "colliders: an empty query finds nothing");
        var rejected = false;
        try { _ = new ColliderQuery(0); } catch (ArgumentOutOfRangeException) { rejected = true; }
        check(rejected, "colliders: a query needs a positive capacity");
        Physics.Hits = Array.Empty<Collider>();

        var growing = new ColliderQuery(2, grow: true);
        Physics.Hits = new[] { a, b };
        check(growing.Sphere(default, 2f, 1, QueryTriggerInteraction.Collide) == 2 && growing.Capacity == 4 && !growing.Truncated, "colliders: an exactly full growing query repeats with a larger buffer, as an exact fit cannot be told from an overflow");
        Physics.Hits = new[] { a, b, c, a, b };
        check(growing.Sphere(default, 2f, 0x77, QueryTriggerInteraction.Ignore) == 5 && growing.Capacity == 8 && !growing.Truncated && growing[4] == b, "colliders: a growing query returns every collider, however many, and never reads as truncated");
        check(Physics.Last.Mask == 0x77 && Physics.Last.Trigger == QueryTriggerInteraction.Ignore, "colliders: the repeats keep the mask and trigger mode");
        Physics.Hits = new[] { c };
        check(growing.Sphere(default, 2f, 1, QueryTriggerInteraction.Ignore) == 1 && growing.Capacity == 8 && growing[0] == c, "colliders: a grown buffer is kept for the next query");
        growing.Clear();
        var cleared = false;
        try { _ = growing[0]; } catch (ArgumentOutOfRangeException) { cleared = true; }
        check(cleared && growing.Count == 0, "colliders: Clear releases the last result");
        Physics.Hits = Array.Empty<Collider>();
    }
}
