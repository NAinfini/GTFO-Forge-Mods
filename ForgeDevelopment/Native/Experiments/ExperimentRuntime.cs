using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using GameData;
using TMPro;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>
/// The game-facing implementation of an experiment: resolve a target, call into live objects, read and write
/// members, build a world event and take screenshots. Every failure is returned as text so one bad step
/// reports a step failure instead of ending the run with an exception.
/// </summary>
internal sealed class ExperimentRuntime : ExperimentEnvironment
{
    private const int MaximumCandidates = 32;

    public long Frame => RecTime.Frame;
    public double Seconds => Time.realtimeSinceStartup;
    /// <summary>The running world's own tick rate, read from the same Unity step the host binding reads it from,
    /// or null when the step is not a usable number. A frame is one of those ticks, so an authored wait in seconds
    /// converts at the rate the world is really advancing with.</summary>
    public double? TicksPerSecond
    {
        get
        {
            try
            {
                float step = Time.fixedDeltaTime;
                return step > 0f && float.IsFinite(step) ? 1d / step : null;
            }
            catch (Exception) { return null; } // Il2Cpp read guard: null is the documented "no usable tick rate" answer.
        }
    }
    public bool IsHost => RecTime.Role == "host";
    public object? LastResult { get; set; }

    internal static readonly ExperimentRuntime Shared = new();

    public ExperimentTargetResult Resolve(ExperimentTargetSpec spec)
    {
        if (spec.Kind != ExperimentTargetKind.PreviousResult) return ExperimentTargeting.Resolve(spec);
        return LastResult == null
            ? ExperimentTargetResult.Failed("no previous call returned a value")
            : ExperimentTargetResult.One("result " + ExperimentValue.Identity(LastResult), LastResult);
    }

    public ExperimentCallResult Call(ExperimentTargetResult target, ExperimentStep step)
    {
        if (step.Name.StartsWith("drive.", StringComparison.Ordinal)) return AuthoringGameDrive.Call(target, step);

        var member = step.Name;
        if (target.IsSet) return ExperimentCallResult.Failed("'" + member + "' needs one object; the target resolved to " + target.Count + " objects");
        var owner = target.Value;
        if (owner == null) return ExperimentCallResult.Failed("the target resolved to nothing");
        var type = owner as Type;
        try
        {
            if (owner is CellMenu.CM_PageLoadout loadout && member == "EXT_StartExpedition"
                && step.Arguments.Count == 0 && !step.ResultArgument)
            {
                if (!ForgeCore.Session.IsHost || GameStateManager.CurrentStateName != eGameStateName.Lobby)
                    return ExperimentCallResult.Failed("experiment.drop-host-lobby-required");
                return ExperimentRunner.ChangeWorld(false, loadout.EXT_StartExpedition);
            }
            if (step.ResultArgument)
            {
                if (LastResult == null) return ExperimentCallResult.Failed("'" + member + "' needs the previous result, but no call has returned one");
                if (!TryInvokeWithArguments(owner, type, member, new[] { LastResult }, out var fromResult, out var resultError))
                    return ExperimentCallResult.Failed(resultError);
                LastResult = fromResult;
                Track(fromResult);
                return ExperimentCallResult.Done(ExperimentValue.Format(fromResult), fromResult);
            }
            if (step.Arguments.Count == 0)
            {
                var read = VanillaValueReading.ReadMember(owner, member);
                if (read.Available) return ExperimentCallResult.Done(ExperimentValue.Format(read.Value), read.Value);
                if (read.Outcome != "missing-member")
                    return ExperimentCallResult.Failed("'" + member + "': " + read.Outcome + ": " + read.Error);
            }
            if (!TryInvoke(owner, type, step, LastResult, out var result, out var error)) return ExperimentCallResult.Failed(error);
            LastResult = result;
            Track(result);
            return ExperimentCallResult.Done(ExperimentValue.Format(result), result);
        }
        catch (Exception e)
        {
            return ExperimentCallResult.Failed("'" + member + "' failed: " + ExperimentValue.Unwrap(e).Message);
        }
    }

    public ExperimentValue.ReadResult Read(ExperimentTargetResult target, string path)
    {
        var result = target.IsSet ? ExperimentObservationRead.ReadMany(target.Many!, path, value => ExperimentValue.Format(value))
            : ExperimentObservationRead.Read(target.Value, path);
        return new ExperimentValue.ReadResult(result.Ok, result.Value, result.Error);
    }

    public ExperimentValue.WriteResult Write(ExperimentTargetResult target, string path, JsonElement literal)
    {
        if (target.IsSet) return new ExperimentValue.WriteResult(false, "a write needs one object; the target resolved to " + target.Count + " objects");
        return ExperimentValue.Write(target.Value, path, literal);
    }

    /// <summary>
    /// Compares the current value against the baseline the engine took when the wait started. Without
    /// <c>changed</c> the step only waits for the path to become readable, which is how "wait until this
    /// exists" is expressed.
    /// </summary>
    public ExperimentWaitResult WaitForPath(ExperimentTargetResult target, string path, bool changed, object? baseline)
    {
        if (target.Value == null) return ExperimentWaitResult.Failed("the target resolved to nothing");
        var read = ExperimentObservationRead.Read(target.Value, path);
        if (!read.Ok) return ExperimentWaitResult.Failed(read.Error);
        var current = read.Value;
        if (current == null) return ExperimentWaitResult.Pending("'" + path + "' is still null");
        var text = "'" + path + "' = " + ExperimentValue.Format(current);
        if (!changed) return ExperimentWaitResult.Complete(text);
        return ValuesEqual(baseline, current) ? ExperimentWaitResult.Pending(text) : ExperimentWaitResult.Complete(text + " (was " + ExperimentValue.Format(baseline) + ")");
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left == null || right == null) return left == null && right == null;
        if (left.Equals(right)) return true;
        // Value types arrive as fresh wrappers on every read, so equality on the printed form is the
        // comparison that actually answers "did this value change".
        return string.Equals(ExperimentValue.Format(left), ExperimentValue.Format(right), StringComparison.Ordinal);
    }

    public ExperimentCallResult WorldEvent(JsonElement fields)
    {
        var data = new WardenObjectiveEventData();
        foreach (var property in fields.EnumerateObject())
        {
            if (property.Name is "Type" or "type")
            {
                if (!ExperimentConvert.TryConvert(property.Value, typeof(eWardenObjectiveEventType), out var type, out var typeError))
                    return ExperimentCallResult.Failed("worldEvent.Type: " + typeError);
                data.Type = (eWardenObjectiveEventType)type!;
                continue;
            }
            if (!ExperimentValue.TryMember(data, property.Name, out var member, out var error))
            {
                var alternate = property.Name.StartsWith('_') ? property.Name[1..] : "_" + property.Name;
                if (!ExperimentValue.TryMember(data, alternate, out member, out error))
                    return ExperimentCallResult.Failed("WardenObjectiveEventData has no field '" + property.Name + "': " + error);
            }
            var name = PlainName(member!);
            if (!ExperimentValue.TryMember(data, name, out var writable, out _)) writable = member;
            var target = ExperimentValue.MemberType(writable!);
            if (!ExperimentConvert.TryConvert(property.Value, target, out var converted, out var conversion))
                return ExperimentCallResult.Failed("worldEvent." + property.Name + ": " + conversion);
            try
            {
                switch (writable)
                {
                    case PropertyInfo info:
                        info.SetValue(data, converted);
                        break;
                    case FieldInfo field:
                        field.SetValue(data, converted);
                        break;
                    default:
                        return ExperimentCallResult.Failed("worldEvent." + property.Name + " is not writable");
                }
            }
            catch (Exception e)
            {
                return ExperimentCallResult.Failed("worldEvent." + property.Name + ": " + ExperimentValue.Unwrap(e).Message);
            }
        }
        try
        {
            WorldEventManager.ExecuteEvent(data, 0f);
        }
        catch (Exception e)
        {
            return ExperimentCallResult.Failed("WorldEventManager.ExecuteEvent failed: " + ExperimentValue.Unwrap(e).Message);
        }
        return ExperimentCallResult.Done("Type=" + data.Type + " Layer=" + data.Layer + " LocalIndex=" + data.LocalIndex +
               // `is null`, not `== null`: the game's LocalizedText equality operator dereferences both sides.
               (data.WardenIntel is null ? "" : " WardenIntel=" + RecReflect.Truncate(data.WardenIntel.ToString() ?? "", 200)), null);
    }

    /// <summary>
    /// Duplicates a live object under a new parent. Unity's Instantiate keeps fonts, materials and TMP
    /// settings, which is exactly what the HUD experiments need; the copy is reported so a later step can
    /// address it as <c>result</c> and the cleanup can destroy it.
    /// </summary>
    public ExperimentCloneResult Clone(ExperimentTargetResult target, string path, string parent, string text)
    {
        if (target.Value == null) return ExperimentCloneResult.Failed("the target resolved to nothing");
        var source = ExperimentValue.Read(target.Value, path);
        if (!source.Ok) return ExperimentCloneResult.Failed(source.Error);
        if (source.Value is not UnityEngine.Object original || original == null)
            return ExperimentCloneResult.Failed("'" + path + "' is not a live Unity object");
        var parentRead = ExperimentValue.Read(target.Value, parent);
        if (!parentRead.Ok) return ExperimentCloneResult.Failed(parentRead.Error);
        if (parentRead.Value is not Component parentComponent || parentComponent == null)
            return ExperimentCloneResult.Failed("'" + parent + "' is not a live component to parent under");
        try
        {
            var copy = UnityEngine.Object.Instantiate(original, parentComponent.transform);
            copy.name = original.name + "-experiment";
            if (copy.TryCast<GameObject>() is { } copyObject) ExperimentObjects.Track(copyObject);
            var report = "cloned " + original.GetType().Name + " under " + ExperimentTargeting.Describe(parentComponent);
            if (text.Length != 0)
            {
                var tmp = copy.TryCast<TextMeshPro>();
                if (tmp == null && copy.TryCast<GameObject>() is { } cloneObject) tmp = cloneObject.GetComponentInChildren<TextMeshPro>();
                if (tmp == null) report += "; no TextMeshPro component, text not set";
                else
                {
                    tmp.text = text;
                    report += "; text set";
                }
            }
            return ExperimentCloneResult.Done(report, copy);
        }
        catch (Exception e)
        {
            return ExperimentCloneResult.Failed("clone failed: " + ExperimentValue.Unwrap(e).Message);
        }
    }

    public string Screenshot(string label)
    {
        try
        {
            var path = RecSession.Screenshot(label);
            return path.Length == 0 ? "<screenshot failed; see the shot channel>" : path;
        }
        catch (Exception e)
        {
            return "<screenshot failed: " + e.Message + ">";
        }
    }

    public bool TraceSeen(string typeName, string methodName) => ExperimentTrace.HasSeen(typeName, methodName);

    /// <summary>Remembers the objects an experiment creates so the runner can destroy them when it ends.</summary>
    private static void Track(object? value)
    {
        switch (value)
        {
            case NavMarker marker:
                ExperimentObjects.Track(marker);
                break;
            case GameObject clone:
                ExperimentObjects.Track(clone);
                break;
        }
    }

    /// <summary>Interop exposes every native field twice, as <c>_Name_k__BackingField</c> and as <c>Name</c>;
    /// the plain property is the one with a setter.</summary>
    private static string PlainName(MemberInfo member)
    {
        const string suffix = "_k__BackingField";
        var name = member.Name;
        return name.Contains(suffix, StringComparison.Ordinal) ? name[..name.IndexOf(suffix, StringComparison.Ordinal)] : name;
    }

    /// <summary>
    /// Picks the overload whose parameters accept the given literals. Named arguments bind to parameters with
    /// that name, which is how an optional parameter past the first defaulted one is reached.
    /// </summary>
    private static bool TryInvoke(object owner, Type? staticType, ExperimentStep step, object? carried, out object? result, out string error)
    {
        result = null;
        error = "";
        var member = step.Name;
        var type = staticType ?? owner.GetType();
        var flags = BindingFlags.Public | BindingFlags.NonPublic | (staticType != null ? BindingFlags.Static : BindingFlags.Instance);
        var candidates = Candidates(type, member, flags);
        if (candidates.Count == 0)
        {
            error = "no method named '" + member + "' on " + type.FullName;
            return false;
        }

        var conversionErrors = new List<string>();
        foreach (var method in candidates.OrderBy(method => method.GetParameters().Length))
        {
            if (!TryBind(method, step, carried, out var converted, out var binding)) continue;
            if (converted == null)
            {
                conversionErrors.Add(binding);
                continue;
            }
            return Invoke(method, owner, staticType, converted, out result, out error);
        }
        error = "no overload of '" + member + "' accepts these arguments. Available: " + Signatures(candidates) +
                (conversionErrors.Count == 0 ? "" : ". Binding: " + string.Join("; ", conversionErrors.Take(4)));
        return false;
    }

    /// <summary>Invokes by position with values this module already holds, as in handing a clone to Destroy.</summary>
    private static bool TryInvokeWithArguments(object owner, Type? staticType, string member, object?[] arguments, out object? result, out string error)
    {
        result = null;
        error = "";
        var type = staticType ?? owner.GetType();
        var flags = BindingFlags.Public | BindingFlags.NonPublic | (staticType != null ? BindingFlags.Static : BindingFlags.Instance);
        var candidates = Candidates(type, member, flags);
        if (candidates.Count == 0)
        {
            error = "no method named '" + member + "' on " + type.FullName;
            return false;
        }
        foreach (var method in candidates.OrderBy(method => method.GetParameters().Length))
        {
            var parameters = method.GetParameters();
            if (parameters.Length != arguments.Length) continue;
            if (parameters.Any(parameter => parameter.ParameterType.IsByRef || parameter.IsOut)) continue;
            var converted = new object?[parameters.Length];
            var usable = true;
            for (var index = 0; index < parameters.Length; index++)
            {
                if (arguments[index] == null && !parameters[index].ParameterType.IsValueType) { converted[index] = null; continue; }
                if (arguments[index] != null && parameters[index].ParameterType.IsInstanceOfType(arguments[index])) { converted[index] = arguments[index]; continue; }
                if (ExperimentConvert.TryConvert(arguments[index], parameters[index].ParameterType, out converted[index], out _)) continue;
                usable = false;
                break;
            }
            if (!usable) continue;
            return Invoke(method, owner, staticType, converted, out result, out error);
        }
        error = "no overload of '" + member + "' accepts the previous result. Available: " + Signatures(candidates);
        return false;
    }

    private static List<MethodInfo> Candidates(Type type, string member, BindingFlags flags)
    {
        var candidates = new List<MethodInfo>();
        for (var current = type; current != null; current = current.BaseType)
            candidates.AddRange(current.GetMethods(flags | BindingFlags.DeclaredOnly).Where(method => method.Name == member));
        return candidates;
    }

    private static string Signatures(IEnumerable<MethodInfo> candidates) => string.Join(" | ", candidates.Take(MaximumCandidates).Select(method =>
        method.Name + "(" + string.Join(", ", method.GetParameters().Select(parameter => parameter.Name + ":" + parameter.ParameterType.Name + (parameter.HasDefaultValue ? "=" : ""))) + ")"));

    private static bool Invoke(MethodInfo method, object owner, Type? staticType, object?[] converted, out object? result, out string error)
    {
        error = "";
        try
        {
            result = method.Invoke(staticType != null ? null : owner, converted);
            return true;
        }
        catch (Exception e)
        {
            result = null;
            var inner = ExperimentValue.Unwrap(e);
            error = method.Name + "(" + string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name)) + ") threw " + inner.GetType().Name + ": " + inner.Message;
            return false;
        }
    }

    private static bool TryBind(MethodInfo method, ExperimentStep step, object? carried, out object?[]? arguments, out string problem)
    {
        arguments = null;
        problem = "";
        var parameters = method.GetParameters();
        if (parameters.Any(parameter => parameter.ParameterType.IsByRef || parameter.IsOut)) return false;
        var bound = new object?[parameters.Length];
        var assigned = new bool[parameters.Length];
        var named = false;
        for (var index = 0; index < step.Arguments.Count; index++)
        {
            var name = index < step.ArgumentNames.Count ? step.ArgumentNames[index] : "";
            var position = index;
            if (name.Length != 0)
            {
                named = true;
                position = Array.FindIndex(parameters, parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase));
                if (position < 0)
                {
                    problem = "no parameter named '" + name + "'";
                    return true;
                }
                if (assigned[position])
                {
                    problem = "parameter '" + name + "' is given twice";
                    return true;
                }
            }
            else if (named || position >= parameters.Length)
            {
                problem = "positional arguments cannot follow a named one";
                return true;
            }
            // A call with result: true hands the object the previous step produced to the first parameter,
            // which is how a marker or a clone is given back to the game for placement or destruction.
            if (step.ResultArgument && step.Arguments[index].ValueKind == JsonValueKind.Null)
            {
                if (carried == null)
                {
                    problem = "parameter '" + parameters[position].Name + "' takes the previous result, but no step produced one";
                    return true;
                }
                bound[position] = carried;
                assigned[position] = true;
                continue;
            }
            if (!ExperimentConvert.TryConvert(step.Arguments[index], parameters[position].ParameterType, out var converted, out var conversion))
            {
                problem = parameters[position].Name + ": " + conversion;
                return true;
            }
            bound[position] = converted;
            assigned[position] = true;
        }
        for (var index = 0; index < parameters.Length; index++)
        {
            if (assigned[index]) continue;
            if (!parameters[index].HasDefaultValue) return false;
            bound[index] = parameters[index].DefaultValue;
        }
        arguments = bound;
        return true;
    }
}
