using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

/// <summary>Recipient-indexed state carried by the existing native-state snapshot, delta, checkpoint and migration
/// paths. Domains own only the payload below each recipient; Runtime owns and validates the recipient envelope.</summary>
internal static partial class DomainStateChannel
{
    public const string RecipientObserverCode = "recipient-state-observer-failed";

    private sealed class RecipientSource
    {
        internal readonly RuntimeKernel Runtime;
        internal readonly string Key;
        internal readonly Predicate<PresentationOutput> Handles;
        internal readonly Action<PresentationOutput, CommandContext> Observe;
        internal readonly Func<JsonElement> Capture;
        internal readonly Action<JsonElement> Restore;
        internal readonly NativeStateSource Native;

        internal RecipientSource(RuntimeKernel runtime, string key, Predicate<PresentationOutput> handles,
            Action<PresentationOutput, CommandContext> observe, Func<JsonElement> capture,
            Action<JsonElement> restore)
        {
            Runtime = runtime; Key = key; Handles = handles; Observe = observe;
            Capture = capture; Restore = restore;
            Native = RegisterNativeState(runtime, key, Snapshot, Apply);
            Native.Detached = () => RemoveRecipientSource(this);
        }

        internal JsonElement Snapshot()
        {
            var state = Capture();
            ValidateRecipientState(state);
            return state.Clone();
        }

        internal void Apply(JsonElement state)
        {
            ValidateRecipientState(state);
            Restore(state.Clone());
        }
    }

    private sealed class RecipientRuntime
    {
        internal readonly RuntimeKernel Kernel;
        internal readonly Func<PresentationOutput, CommandResult?> Admission;
        internal readonly List<RecipientSource> Sources = new();
        // The sources one admission observes and the request they read, reused by every admission: a source's
        // Observe runs inside the kernel's context read and never admits another presentation.
        private readonly List<RecipientSource> _selected = new();
        private PresentationOutput? _request;
        private readonly Action<CommandContext> _observe;

        internal RecipientRuntime(RuntimeKernel kernel)
        {
            Kernel = kernel;
            Admission = Admit;
            _observe = Observe;
        }

        /// <summary>Each matching source observes the admitted request. A source validates before it changes
        /// anything, so a refusal leaves its state as it was, and it reports its own change
        /// (<see cref="NativeStateSource.Changed"/>): the admission takes no snapshot and compares none.</summary>
        internal CommandResult? Admit(PresentationOutput request)
        {
            _selected.Clear();
            try
            {
                for (var index = 0; index < Sources.Count; index++)
                    if (Sources[index].Handles(request)) _selected.Add(Sources[index]);
            }
            catch (Exception error)
            { return CommandResult.Rejected(RecipientObserverCode, CommandResult.TruncateDetail(error.GetType().Name)); }
            if (_selected.Count == 0) return null;
            _request = request;
            try { Kernel.WithRecipientContext(request, _observe); return null; }
            catch (Exception error)
            { return CommandResult.Rejected(RecipientObserverCode, CommandResult.TruncateDetail(error.GetType().Name + ":" + error.Message)); }
            finally { _request = null; _selected.Clear(); }
        }

        private void Observe(CommandContext context)
        {
            for (var index = 0; index < _selected.Count; index++) _selected[index].Observe(_request!, context);
        }
    }

    private static readonly Dictionary<RuntimeKernel, RecipientRuntime> RecipientRuntimes =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Register one recipient-indexed domain source. Capture and restore use the strict envelope
    /// <c>{ "recipients": { "stable-player-id": { ... } } }</c>. Observe runs synchronously for matching
    /// presentation intents after transport admission and before the request is queued for sending. It validates
    /// before it changes anything, so a throw refuses the request and leaves the state as it was, and it reports its
    /// own change with <see cref="NativeStateSource.Changed"/> on the source this returns; disposing that source
    /// removes the recipient source as well.</summary>
    public static NativeStateSource RegisterRecipientState(IForgeEngine runtime, string key,
        Predicate<PresentationOutput> handles, Action<PresentationOutput, CommandContext> observe,
        Func<JsonElement> capture, Action<JsonElement> restore)
    {
        if (runtime is not RuntimeKernel kernel) throw new ArgumentException("recipient-state-runtime", nameof(runtime));
        if (string.IsNullOrWhiteSpace(key) || handles == null || observe == null || capture == null || restore == null)
            throw new ArgumentException("recipient-state-source");
        var source = new RecipientSource(kernel, key, handles, observe, capture, restore);
        try
        {
            if (!RecipientRuntimes.TryGetValue(kernel, out var state))
            {
                state = new RecipientRuntime(kernel);
                RecipientRuntimes.Add(kernel, state);
                kernel.RecipientStateAdmission = state.Admission;
            }
            state.Sources.Add(source);
            return source.Native;
        }
        catch
        {
            source.Native.Dispose();
            throw;
        }
    }

    /// <summary>Read a newly admitted request with its loaded plan constants resolved.</summary>
    public static void WithRecipientContext(IForgeEngine runtime, PresentationOutput request,
        Action<CommandContext> read)
    {
        if (runtime is not RuntimeKernel kernel) throw new ArgumentException("recipient-state-runtime", nameof(runtime));
        kernel.WithRecipientContext(request, read);
    }

    /// <summary>Read a retained request after its complete input document was rebound to the current world.</summary>
    public static void RebuildRecipientContext(IForgeEngine runtime, PresentationOutput request,
        JsonElement inputs, JsonElement parameters, Action<CommandContext> read)
    {
        if (runtime is not RuntimeKernel kernel) throw new ArgumentException("recipient-state-runtime", nameof(runtime));
        kernel.RebuildRecipientContext(request, inputs, parameters, read);
    }

    private static void RemoveRecipientSource(RecipientSource source)
    {
        if (!RecipientRuntimes.TryGetValue(source.Runtime, out var state)) return;
        state.Sources.Remove(source);
        if (state.Sources.Count != 0) return;
        if (ReferenceEquals(source.Runtime.RecipientStateAdmission, state.Admission))
            source.Runtime.RecipientStateAdmission = null;
        RecipientRuntimes.Remove(source.Runtime);
    }

    private static bool RecipientStateRequired(IForgeEngine runtime, string key)
        => runtime is RuntimeKernel kernel && RecipientRuntimes.TryGetValue(kernel, out var state)
            && state.Sources.Any(source => source.Key == key);

    private static void ValidateRecipientState(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object)
            throw new RuntimeContractException("native-state-object", "Recipient state must be an object.");
        JsonElement recipients = default;
        var root = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in state.EnumerateObject())
        {
            if (!root.Add(property.Name) || property.Name != "recipients")
                throw new RuntimeContractException("native-state-object", "Unexpected recipient-state member: " + property.Name);
            recipients = property.Value;
        }
        if (!root.Contains("recipients") || recipients.ValueKind != JsonValueKind.Object)
            throw new RuntimeContractException("native-state-object", "Recipient state needs a recipients object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var recipient in recipients.EnumerateObject())
        {
            if (++count > RuntimeKernel.MaximumPresentationRecipients)
                throw new RuntimeContractException("native-state-object",
                    "Recipient state exceeds the presentation recipient bound.");
            if (string.IsNullOrWhiteSpace(recipient.Name) || recipient.Name.Length > 128 || !names.Add(recipient.Name))
                throw new RuntimeContractException("native-state-object", "Invalid recipient identity: " + recipient.Name);
            if (recipient.Value.ValueKind != JsonValueKind.Object)
                throw new RuntimeContractException("native-state-object", "Recipient payload must be an object: " + recipient.Name);
        }
    }
}
