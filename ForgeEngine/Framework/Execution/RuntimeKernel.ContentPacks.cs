using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeModuleHandle
{
    /// <summary>Owns one content domain during the registration window. The host calls the consumer once for each
    /// validated entry of every content generation (contract I-RELOAD): the first after registration closes, before plans
    /// load, and again for each later generation an author commits in a clean lobby. Within one generation the callback must
    /// explicitly accept or reject and is never retried. Referenced entries must be accepted first; self-references are local
    /// to this entry, and cross-entry cycles are refused. A domain must not claim native game registration merely because it
    /// received JSON.</summary>
    public void RegisterContentDomain(string domain, Func<RuntimeContentEntry, ContentRegistrationResult> consumer)
        => kernel.RegisterContentDomain(this, domain, consumer);
}

public sealed partial class RuntimeKernel
{
    private readonly Dictionary<string, (RuntimeModuleHandle Owner, Func<RuntimeContentEntry, ContentRegistrationResult> Consume)>
        contentConsumers = new(StringComparer.Ordinal);
    private bool contentDiscovered, contentDispatching;

    internal void RequireContentHostAccess(bool registering)
    {
        Mutable();
        RuntimeJson.Require(StartupState == (registering ? RuntimeStartupState.Registering : RuntimeStartupState.Starting),
            "runtime-not-ready", "Host content callbacks are startup-only.");
    }

    /// <summary>One generation's discovery: the first during startup, every later one once the host is ready.</summary>
    internal void BeginContentDiscovery()
    {
        Mutable();
        RuntimeJson.Require(StartupState is RuntimeStartupState.Starting or RuntimeStartupState.Ready, "runtime-not-ready",
            "Content is delivered during startup or to a ready host.");
        RuntimeJson.Require(!contentDiscovered && !contentDispatching, "content-already-dispatched", "A content generation is already being delivered.");
        contentDiscovered = true;
    }

    internal void RegisterContentDomain(RuntimeModuleHandle owner, string domain,
        Func<RuntimeContentEntry, ContentRegistrationResult> consumer)
    {
        Mutable(); ArgumentNullException.ThrowIfNull(consumer);
        RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
        RuntimeJson.Require(IsRegistrationOpen, "registration-closed", "Content consumers register before host startup.");
        RuntimeJson.Require(domain is "weapon" or "enemy" or "behavior", "content-domain", "Unsupported content domain.");
        if (contentConsumers.TryGetValue(domain, out var previous) && !previous.Owner.IsRegistered)
            contentConsumers.Remove(domain);
        RuntimeJson.Require(!contentConsumers.ContainsKey(domain), "content-domain-conflict", domain);
        contentConsumers.Add(domain, (owner, consumer));
    }

    /// <summary>Host-only delivery of one content generation. No entry catalog or content JSON is retained by the kernel;
    /// the consumers stay registered for the next generation and are released when the runtime stops.</summary>
    internal void DispatchContent(IReadOnlyList<RuntimeContentEntry> entries,
        Action<RuntimeContentEntry, ContentRegistrationResult> report)
    {
        Mutable(); ArgumentNullException.ThrowIfNull(entries); ArgumentNullException.ThrowIfNull(report);
        RuntimeJson.Require(StartupState is RuntimeStartupState.Starting or RuntimeStartupState.Ready, "runtime-not-ready",
            "Content is delivered during startup or to a ready host.");
        RuntimeJson.Require(contentDiscovered && !contentDispatching, "content-already-dispatched", "Content delivery requires its generation's discovery.");
        contentDiscovered = false; contentDispatching = true;
        // This is one bounded startup work set, not a persistent content registry. A consumer never sees an entry
        // whose prerequisite was refused, even if it sorts earlier than that prerequisite in the installed files.
        var pending = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var results = new Dictionary<string, ContentRegistrationResult>(StringComparer.Ordinal);
        var ordered = entries.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
        void Finish(RuntimeContentEntry entry, ContentRegistrationResult result)
        {
            pending.Remove(entry.Id); results.Add(entry.Id, result); report(entry, result);
        }
        try
        {
            while (pending.Count != 0)
            {
                bool progress = false;
                foreach (var entry in ordered)
                {
                    if (!pending.ContainsKey(entry.Id)) continue;
                    var rejected = entry.References.FirstOrDefault(reference => reference != entry.Id
                        && results.TryGetValue(reference, out var prerequisite) && !prerequisite.Accepted);
                    if (rejected != null)
                    {
                        Finish(entry, ContentRegistrationResult.Reject("content-entry-missing", rejected));
                        progress = true; continue;
                    }
                    // Discovery already refused unknown references. This defensive check keeps the host-only boundary
                    // explicit if a future caller supplies a malformed snapshot instead of going through discovery.
                    var missing = entry.References.FirstOrDefault(reference => reference != entry.Id
                        && !pending.ContainsKey(reference) && !results.ContainsKey(reference));
                    if (missing != null)
                    {
                        Finish(entry, ContentRegistrationResult.Reject("content-entry-missing", missing));
                        progress = true; continue;
                    }
                    if (entry.References.Any(reference => reference != entry.Id && pending.ContainsKey(reference))) continue;
                    Finish(entry, ConsumeContent(entry)); progress = true;
                }
                if (progress) continue;
                // Only cycles can remain after no entry advanced. Refuse the actual cycle first; the next pass
                // propagates its refusal to downstream dependents rather than mislabelling them as cycle members.
                var cycle = ContentCycle(pending);
                foreach (var id in cycle.OrderBy(id => id, StringComparer.Ordinal))
                {
                    var entry = pending[id];
                    var other = entry.References.Where(reference => reference != id && cycle.Contains(reference)).OrderBy(reference => reference, StringComparer.Ordinal).First();
                    Finish(entry, ContentRegistrationResult.Reject("content-reference-cycle", other));
                }
            }
        }
        finally { contentDispatching = false; }
    }

    private ContentRegistrationResult ConsumeContent(RuntimeContentEntry entry)
    {
        if (!contentConsumers.TryGetValue(entry.Domain, out var consumer) || !consumer.Owner.IsRegistered)
            return ContentRegistrationResult.Reject("content-domain-unavailable", entry.Domain);
        try
        {
            var result = consumer.Consume(entry);
            if (!consumer.Owner.IsRegistered) return ContentRegistrationResult.Reject("content-domain-unavailable", entry.Domain);
            return ValidContentResult(result) ? result : ContentRegistrationResult.Reject("content-registration-invalid", consumer.Owner.ProviderId);
        }
        catch (Exception error)
        {
            log.LogCallbackFailed("content-registration-failed provider=" + consumer.Owner.ProviderId + " domain=" + entry.Domain, error);
            return ContentRegistrationResult.Reject("content-registration-failed", consumer.Owner.ProviderId);
        }
    }

    private static HashSet<string> ContentCycle(IReadOnlyDictionary<string, RuntimeContentEntry> pending)
    {
        var references = pending.ToDictionary(row => row.Key, row => row.Value.References, StringComparer.Ordinal);
        return ContentReferenceGraph.Cycles(references).FirstOrDefault()
            ?? throw new InvalidOperationException("Unresolved content has no dependency cycle.");
    }

    private static bool ValidContentResult(ContentRegistrationResult result)
    {
        if (string.IsNullOrEmpty(result.Code) || result.Code.Length > 64 || RuntimeLogValue.AccountShaped(result.Code)) return false;
        if (result.Accepted) return result.Code == "content-accepted" && string.IsNullOrEmpty(result.Other);
        if (result.Code == "content-accepted" || result.Code[0] is < 'a' or > 'z' || result.Code[^1] == '-') return false;
        bool hyphen = false;
        foreach (var character in result.Code)
        {
            if (character == '-') { if (hyphen) return false; hyphen = true; }
            else { if (character is not (>= 'a' and <= 'z' or >= '0' and <= '9')) return false; hyphen = false; }
        }
        return true;
    }

    internal void ClearContentConsumers() => contentConsumers.Clear();
}
