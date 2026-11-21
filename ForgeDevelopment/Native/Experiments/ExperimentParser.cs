using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ForgeDevelopment.Native;

/// <summary>
/// Structural parsing and validation of command files. Everything that can be decided without the game is
/// decided here, so a bad file is reported once at load instead of failing in the middle of a run.
/// </summary>
internal static class ExperimentParser
{
    internal const int MaximumCommandsPerFile = 64;
    internal const int MaximumSteps = 256;
    internal const int MaximumRepeatTimes = 1024;
    internal const int MaximumRepeatDepth = 4;
    internal const int MaximumExpandedSteps = 4096;
    internal const int MaximumPathsPerStep = 64;
    internal const int MaximumArguments = 16;
    /// <summary>Unattended runners cancel a command after this many seconds unless the command declares its own bound.</summary>
    internal const int DefaultTimeoutSeconds = 180;
    internal const int MaximumTimeoutSeconds = 4 * 3600;

    private static readonly HashSet<string> CommandKeys = new(StringComparer.Ordinal)
    { "id", "title", "points", "requires", "host", "target", "timeoutSeconds", "before", "steps", "cleanup", "notes" };

    internal static IReadOnlyList<ExperimentCommand> Parse(string json, string source, out string error)
    {
        error = "";
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            error = source + ": invalid JSON at " + e.Message;
            return Array.Empty<ExperimentCommand>();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var commands = new List<ExperimentCommand>();
                var index = 0;
                foreach (var element in root.EnumerateArray())
                    commands.Add(ParseCommand(element, source, "#" + index++.ToString(CultureInfo.InvariantCulture)));
                return commands;
            }
            if (root.ValueKind == JsonValueKind.Object) return new[] { ParseCommand(root, source, "") };
            error = source + ": the root must be an object or an array of objects";
            return Array.Empty<ExperimentCommand>();
        }
    }

    private static ExperimentCommand ParseCommand(JsonElement element, string source, string suffix)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return Invalid(source, suffix, "a command must be a JSON object");
        if (element.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.String)
            suffix = ":" + idNode.GetString();
        var problems = new List<string>();
        foreach (var property in element.EnumerateObject())
            if (!CommandKeys.Contains(property.Name)) problems.Add("unknown key '" + property.Name + "'");

        var id = Text(element, "id", "", problems, required: true);
        if (id.Length > 0 && !IsIdentifier(id)) problems.Add("id '" + id + "' must use letters, digits, '-' or '_'");
        var host = ExperimentHost.Any;
        var hostText = Text(element, "host", "any", problems, required: false);
        switch (hostText)
        {
            case "any": break;
            case "host": host = ExperimentHost.Host; break;
            case "client": host = ExperimentHost.Client; break;
            default: problems.Add("host '" + hostText + "' must be any, host or client"); break;
        }
        var target = Text(element, "target", "", problems, required: true);
        if (target.Length > 0 && !ExperimentTargetSpec.TryParse(target, out _))
            problems.Add("target '" + target + "' is not a known selector: " + ExperimentTargetSpec.DescribeSyntax());
        var timeoutSeconds = DefaultTimeoutSeconds;
        if (element.TryGetProperty("timeoutSeconds", out var timeoutNode)
            && (timeoutNode.ValueKind != JsonValueKind.Number || !timeoutNode.TryGetInt32(out timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > MaximumTimeoutSeconds))
        {
            problems.Add("timeoutSeconds must be an integer between 1 and " + MaximumTimeoutSeconds);
            timeoutSeconds = DefaultTimeoutSeconds;
        }
        var before = ParseStage(element, "before", problems);
        var steps = ParseSteps(element, "steps", problems, true);
        var cleanup = ParseSteps(element, "cleanup", problems, false);
        if (steps.Count == 0) problems.Add("steps must contain at least one step");
        var expanded = 0;
        ValidateExpansion(steps, 0, ref expanded, problems);
        ValidateExpansion(cleanup, 0, ref expanded, problems);
        if (expanded > MaximumExpandedSteps) problems.Add("the command expands to " + expanded + " steps; the limit is " + MaximumExpandedSteps);
        return new ExperimentCommand
        {
            Id = id,
            Title = Text(element, "title", id, problems, required: false),
            Points = TextArray(element, "points", problems),
            Requires = TextArray(element, "requires", problems),
            Host = host,
            Target = target,
            TimeoutSeconds = timeoutSeconds,
            Before = before,
            Steps = steps,
            Cleanup = cleanup,
            Problems = problems,
            Source = source + suffix
        };
    }

    private static ExperimentCommand Invalid(string source, string suffix, string problem) => new()
    {
        Id = "",
        Source = source + suffix,
        Problems = new[] { problem }
    };

    private static ExperimentStage ParseStage(JsonElement parent, string name, List<string> problems)
    {
        if (!parent.TryGetProperty(name, out var node) || node.ValueKind == JsonValueKind.Null) return ExperimentStage.Empty;
        if (node.ValueKind != JsonValueKind.Object)
        {
            problems.Add(name + " must be an object");
            return ExperimentStage.Empty;
        }
        var snapshot = new List<string>();
        var screenshot = false;
        foreach (var property in node.EnumerateObject())
        {
            switch (property.Name)
            {
                case "snapshot":
                    foreach (var path in ReadStringArray(property.Value, name + ".snapshot", problems)) snapshot.Add(path);
                    foreach (var path in snapshot)
                        if (!ExperimentPath.IsValid(path)) problems.Add(name + ".snapshot path '" + path + "' is not a dotted path");
                    break;
                case "screenshot":
                    if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) screenshot = property.Value.GetBoolean();
                    else problems.Add(name + ".screenshot must be a boolean");
                    break;
                default:
                    problems.Add("unknown key '" + name + "." + property.Name + "'");
                    break;
            }
        }
        return new ExperimentStage { Snapshot = snapshot, Screenshot = screenshot };
    }

    private static IReadOnlyList<ExperimentStep> ParseSteps(JsonElement parent, string name, List<string> problems, bool required)
    {
        if (!parent.TryGetProperty(name, out var node))
        {
            if (required) problems.Add(name + " is missing");
            return Array.Empty<ExperimentStep>();
        }
        return ParseStepArray(node, name, problems, 0);
    }

    private static IReadOnlyList<ExperimentStep> ParseStepArray(JsonElement node, string where, List<string> problems, int depth)
    {
        if (node.ValueKind == JsonValueKind.Null) return Array.Empty<ExperimentStep>();
        if (node.ValueKind != JsonValueKind.Array)
        {
            problems.Add(where + " must be an array of steps");
            return Array.Empty<ExperimentStep>();
        }
        if (depth > MaximumRepeatDepth)
        {
            problems.Add(where + " nests repeats deeper than " + MaximumRepeatDepth);
            return Array.Empty<ExperimentStep>();
        }
        var steps = new List<ExperimentStep>();
        var index = 0;
        foreach (var element in node.EnumerateArray())
        {
            var at = where + "[" + index++.ToString(CultureInfo.InvariantCulture) + "]";
            if (steps.Count >= MaximumSteps)
            {
                problems.Add(where + " has more than " + MaximumSteps + " steps");
                break;
            }
            var step = ParseStep(element, at, problems, depth);
            if (step != null) steps.Add(step);
        }
        return steps;
    }

    private static ExperimentStep? ParseStep(JsonElement element, string at, List<string> problems, int depth)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var command = element.GetString() ?? "";
            if (!command.StartsWith("screenshot", StringComparison.Ordinal))
            {
                problems.Add(at + " string steps only support \"screenshot\"");
                return null;
            }
            var reason = command.Length > "screenshot".Length ? command["screenshot".Length..].TrimStart(':', ' ') : "";
            return new ExperimentStep { Kind = ExperimentStepKind.Screenshot, Reason = reason, Node = at };
        }
        if (element.ValueKind != JsonValueKind.Object)
        {
            problems.Add(at + " must be an object");
            return null;
        }

        var keys = new List<string>();
        foreach (var property in element.EnumerateObject()) keys.Add(property.Name);
        var payloadKeys = new List<string>();
        foreach (var key in keys)
            if (key is "call" or "set" or "read" or "wait" or "screenshot" or "worldEvent" or "repeat" or "clone") payloadKeys.Add(key);
        if (payloadKeys.Count == 0)
        {
            problems.Add(at + " has no step type; expected call, set, read, wait, screenshot, worldEvent, repeat or clone");
            return null;
        }
        // "screenshot": true inside a read step is that step's snapshot flag, not a second step type.
        if (payloadKeys.Count == 2 && payloadKeys.Contains("read") && payloadKeys.Contains("screenshot"))
            payloadKeys.Remove("screenshot");
        if (payloadKeys.Count > 1)
        {
            problems.Add(at + " combines " + string.Join(", ", payloadKeys) + "; a step has exactly one type");
            return null;
        }
        foreach (var key in keys)
        {
            var owner = key switch
            {
                "call" or "set" or "read" or "wait" or "screenshot" or "worldEvent" or "repeat" or "clone" or "target" => "",
                "args" or "named" or "result" => "call",
                "paths" => "read",
                "path" or "value" => "set",
                "parent" or "text" => "clone",
                _ => "unknown"
            };
            if (owner == "unknown") problems.Add(at + " has unknown key '" + key + "'");
            else if (owner.Length != 0 && owner != payloadKeys[0]) problems.Add(at + " carries '" + key + "' which only belongs to " + owner);
        }

        var kind = payloadKeys[0];
        var stepTarget = "";
        if (element.TryGetProperty("target", out var targetNode))
        {
            if (targetNode.ValueKind != JsonValueKind.String) problems.Add(at + ".target must be a string");
            else
            {
                stepTarget = targetNode.GetString() ?? "";
                if (stepTarget != "result" && !ExperimentTargetSpec.TryParse(stepTarget, out _))
                    problems.Add(at + ".target '" + stepTarget + "' is not a known selector: " + ExperimentTargetSpec.DescribeSyntax());
            }
        }
        switch (kind)
        {
            case "call":
                {
                    var name = Text(element, "call", "", problems, required: true, at);
                    if (name.Length > 0 && !ExperimentPath.IsValidCall(name)) problems.Add(at + ".call '" + name + "' is not a dotted member name");
                    var arguments = new List<JsonElement>();
                    var names = new List<string>();
                    if (element.TryGetProperty("args", out var argsNode))
                    {
                        if (argsNode.ValueKind != JsonValueKind.Array) problems.Add(at + ".args must be an array");
                        else foreach (var argument in argsNode.EnumerateArray())
                        {
                            if (arguments.Count >= MaximumArguments) { problems.Add(at + ".args has more than " + MaximumArguments + " entries"); break; }
                            arguments.Add(argument.Clone());
                            names.Add("");
                        }
                    }
                    // {"name": value} binds a parameter by name, which is how an optional parameter past the
                    // first defaulted argument is reached without writing every parameter in between.
                    if (element.TryGetProperty("named", out var namedNode))
                    {
                        if (namedNode.ValueKind != JsonValueKind.Object) problems.Add(at + ".named must be an object");
                        else foreach (var entry in namedNode.EnumerateObject())
                        {
                            if (arguments.Count >= MaximumArguments) { problems.Add(at + ".named has more than " + MaximumArguments + " entries"); break; }
                            if (!ExperimentPath.IsValid(entry.Name)) problems.Add(at + ".named key '" + entry.Name + "' is not a parameter name");
                            if (names.Contains(entry.Name)) problems.Add(at + ".named binds '" + entry.Name + "' twice");
                            arguments.Add(entry.Value.Clone());
                            names.Add(entry.Name);
                        }
                    }
                    var resultArgument = false;
                    if (element.TryGetProperty("result", out var resultNode))
                    {
                        if (resultNode.ValueKind is JsonValueKind.True or JsonValueKind.False) resultArgument = resultNode.GetBoolean();
                        else problems.Add(at + ".result must be a boolean");
                        if (resultArgument && arguments.Count != 0) problems.Add(at + " cannot use args or named together with result");
                    }
                    return new ExperimentStep
                    {
                        Kind = ExperimentStepKind.Call,
                        Name = name,
                        Arguments = arguments,
                        ArgumentNames = names,
                        ResultArgument = resultArgument,
                        Node = at,
                        Target = stepTarget
                    };
                }
            case "set":
                {
                    var path = Text(element, "path", "", problems, required: true, at);
                    if (path.Length > 0 && !ExperimentPath.IsValid(path)) problems.Add(at + ".path '" + path + "' is not a dotted path");
                    if (!element.TryGetProperty("value", out var valueNode)) problems.Add(at + ".value is missing");
                    return new ExperimentStep
                    {
                        Kind = ExperimentStepKind.Set,
                        Path = path,
                        Literal = valueNode.ValueKind == JsonValueKind.Undefined ? null : valueNode.Clone(),
                        Node = at,
                        Target = stepTarget
                    };
                }
            case "read":
                {
                    var paths = new List<string>();
                    if (element.TryGetProperty("paths", out var pathsNode))
                        foreach (var path in ReadStringArray(pathsNode, at + ".paths", problems)) paths.Add(path);
                    else if (element.TryGetProperty("read", out var inlineNode) && inlineNode.ValueKind != JsonValueKind.True && inlineNode.ValueKind != JsonValueKind.False)
                        foreach (var path in ReadStringArray(inlineNode, at + ".read", problems)) paths.Add(path);
                    else problems.Add(at + " reads nothing; give \"read\": [\"path\", ...] or \"read\": \"path\"");
                    if (paths.Count > MaximumPathsPerStep) problems.Add(at + " reads more than " + MaximumPathsPerStep + " paths");
                    foreach (var path in paths)
                        if (!ExperimentPath.IsValid(path)) problems.Add(at + " path '" + path + "' is not a dotted path");
                    var screenshot = false;
                    if (element.TryGetProperty("screenshot", out var shotNode))
                    {
                        if (shotNode.ValueKind is JsonValueKind.True or JsonValueKind.False) screenshot = shotNode.GetBoolean();
                        else problems.Add(at + ".screenshot must be a boolean");
                    }
                    return new ExperimentStep { Kind = ExperimentStepKind.Read, Paths = paths, Screenshot = screenshot, Node = at, Target = stepTarget };
                }
            case "wait":
                return new ExperimentStep { Kind = ExperimentStepKind.Wait, Wait = ParseWait(element.GetProperty("wait"), at + ".wait", problems), Node = at, Target = stepTarget };
            case "screenshot":
                {
                    var reason = "";
                    var node = element.GetProperty("screenshot");
                    if (node.ValueKind == JsonValueKind.String) reason = node.GetString() ?? "";
                    else if (node.ValueKind is JsonValueKind.True or JsonValueKind.False) { }
                    else if (node.ValueKind != JsonValueKind.Null) problems.Add(at + ".screenshot must be a string or boolean");
                    return new ExperimentStep { Kind = ExperimentStepKind.Screenshot, Reason = reason, Node = at, Target = stepTarget };
                }
            case "worldEvent":
                {
                    var node = element.GetProperty("worldEvent");
                    if (node.ValueKind != JsonValueKind.Object)
                    {
                        problems.Add(at + ".worldEvent must be an object of WardenObjectiveEventData fields");
                        return null;
                    }
                    if (node.TryGetProperty("Type", out var typeNode) || node.TryGetProperty("type", out typeNode))
                        if (typeNode.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
                            problems.Add(at + ".worldEvent.Type must be a number or an enum name");
                    return new ExperimentStep { Kind = ExperimentStepKind.WorldEvent, Literal = node.Clone(), Node = at, Target = stepTarget };
                }
            case "clone":
                {
                    var path = Text(element, "clone", "", problems, required: true, at);
                    if (path.Length > 0 && !ExperimentPath.IsValid(path)) problems.Add(at + ".clone '" + path + "' is not a dotted path");
                    var parent = Text(element, "parent", "", problems, required: true, at);
                    if (parent.Length > 0 && !ExperimentPath.IsValid(parent)) problems.Add(at + ".parent '" + parent + "' is not a dotted path");
                    return new ExperimentStep
                    {
                        Kind = ExperimentStepKind.Clone,
                        Path = path,
                        CloneParent = parent,
                        CloneText = Text(element, "text", "", problems, required: false, at),
                        Node = at,
                        Target = stepTarget
                    };
                }
            case "repeat":
                {
                    var node = element.GetProperty("repeat");
                    if (node.ValueKind != JsonValueKind.Object)
                    {
                        problems.Add(at + ".repeat must be an object with times and steps");
                        return null;
                    }
                    foreach (var property in node.EnumerateObject())
                        if (property.Name is not ("times" or "steps" or "interval"))
                            problems.Add(at + ".repeat has unknown key '" + property.Name + "'");
                    var times = 1;
                    if (!node.TryGetProperty("times", out var timesNode) || timesNode.ValueKind != JsonValueKind.Number || !timesNode.TryGetInt32(out times))
                        problems.Add(at + ".repeat.times must be an integer");
                    else if (times < 1 || times > MaximumRepeatTimes)
                        problems.Add(at + ".repeat.times must be between 1 and " + MaximumRepeatTimes);
                    if (times < 1) times = 1;
                    if (node.TryGetProperty("interval", out var intervalNode) &&
                        (intervalNode.ValueKind != JsonValueKind.Number || !intervalNode.TryGetInt32(out _)))
                        problems.Add(at + ".repeat.interval must be an integer frame count");
                    if (!node.TryGetProperty("steps", out var bodyNode)) problems.Add(at + ".repeat.steps is missing");
                    var body = bodyNode.ValueKind == JsonValueKind.Undefined
                        ? Array.Empty<ExperimentStep>()
                        : ParseStepArray(bodyNode, at + ".repeat.steps", problems, depth + 1);
                    if (body.Count == 0) problems.Add(at + ".repeat.steps is empty");
                    return new ExperimentStep { Kind = ExperimentStepKind.Repeat, Body = body, Times = times, Node = at, Target = stepTarget };
                }
            default:
                problems.Add(at + " has unknown step type '" + kind + "'");
                return null;
        }
    }

    private static ExperimentWait ParseWait(JsonElement node, string at, List<string> problems)
    {
        if (node.ValueKind == JsonValueKind.Number)
        {
            if (!node.TryGetInt32(out var bareFrames) || bareFrames < 0)
            {
                problems.Add(at + " must be a non-negative frame count");
                return ExperimentWait.None;
            }
            return new ExperimentWait { Frames = bareFrames };
        }
        if (node.ValueKind != JsonValueKind.Object)
        {
            problems.Add(at + " must be an object or a frame count");
            return ExperimentWait.None;
        }
        int? frames = null;
        double? seconds = null;
        var traceType = "";
        var traceMethod = "";
        var path = "";
        var changed = false;
        var timeout = 0;
        var traceGiven = false;
        foreach (var property in node.EnumerateObject())
        {
            switch (property.Name)
            {
                case "frames":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var frameValue) || frameValue < 0)
                        problems.Add(at + ".frames must be a non-negative integer");
                    else frames = frameValue;
                    break;
                case "seconds":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out var secondValue) || secondValue < 0 || double.IsNaN(secondValue))
                        problems.Add(at + ".seconds must be a non-negative number");
                    else seconds = secondValue;
                    break;
                case "trace":
                    traceGiven = true;
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        problems.Add(at + ".trace must be \"Type.Method\"");
                        break;
                    }
                    var text = property.Value.GetString() ?? "";
                    var dot = text.LastIndexOf('.');
                    if (dot <= 0 || dot == text.Length - 1) problems.Add(at + ".trace '" + text + "' must be \"Type.Method\"");
                    else
                    {
                        traceType = text[..dot];
                        traceMethod = text[(dot + 1)..];
                    }
                    break;
                case "timeoutFrames":
                case "timeout":
                    var name = property.Name;
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out timeout) || timeout < 0)
                    {
                        problems.Add(at + "." + name + " must be a non-negative integer");
                        timeout = 0;
                    }
                    break;
                case "path":
                    if (property.Value.ValueKind != JsonValueKind.String) problems.Add(at + ".path must be a string");
                    else path = property.Value.GetString() ?? "";
                    break;
                case "changed":
                    if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) changed = property.Value.GetBoolean();
                    else problems.Add(at + ".changed must be a boolean");
                    break;
                default:
                    problems.Add(at + " has unknown key '" + property.Name + "'");
                    break;
            }
        }
        var conditions = (frames == null ? 0 : 1) + (seconds == null ? 0 : 1) + (traceGiven ? 1 : 0) + (path.Length == 0 ? 0 : 1);
        if (conditions == 0) problems.Add(at + " needs frames, seconds, trace or path");
        if (conditions > 1) problems.Add(at + " combines several wait conditions; use one of frames, seconds, trace or path");
        if (path.Length > 0 && !ExperimentPath.IsValid(path)) problems.Add(at + ".path '" + path + "' is not a dotted path");
        if ((traceGiven || path.Length > 0) && timeout <= 0) problems.Add(at + " needs a positive timeoutFrames so a missed condition cannot stall the run");
        return new ExperimentWait
        {
            Frames = frames,
            Seconds = seconds,
            TraceType = traceType,
            TraceMethod = traceMethod,
            Path = path,
            Changed = changed,
            TimeoutFrames = timeout
        };
    }

    private static bool ValidateExpansion(IReadOnlyList<ExperimentStep> steps, int depth, ref int expanded, List<string> problems)
    {
        if (depth > MaximumRepeatDepth)
        {
            problems.Add("repeat nesting exceeds " + MaximumRepeatDepth);
            return false;
        }
        foreach (var step in steps)
        {
            if (step.Kind != ExperimentStepKind.Repeat)
            {
                expanded++;
                continue;
            }
            var before = expanded;
            for (var iteration = 0; iteration < step.Times; iteration++)
            {
                if (expanded - before > MaximumExpandedSteps) break;
                if (!ValidateExpansion(step.Body, depth + 1, ref expanded, problems)) return false;
            }
        }
        return true;
    }

    internal static bool IsIdentifier(string text)
    {
        if (text.Length == 0) return false;
        foreach (var c in text)
            if (!(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')) return false;
        return true;
    }

    private static string Text(JsonElement element, string name, string fallback, List<string> problems, bool required, string at = "")
    {
        var where = at.Length == 0 ? name : at + "." + name;
        if (!element.TryGetProperty(name, out var node))
        {
            if (required) problems.Add(where + " is missing");
            return fallback;
        }
        if (node.ValueKind != JsonValueKind.String)
        {
            problems.Add(where + " must be a string");
            return fallback;
        }
        var value = node.GetString() ?? "";
        if (required && value.Length == 0) problems.Add(where + " must not be empty");
        return value;
    }

    private static IReadOnlyList<string> TextArray(JsonElement element, string name, List<string> problems)
    {
        if (!element.TryGetProperty(name, out var node)) return Array.Empty<string>();
        var values = ReadStringArray(node, name, problems);
        if (values.Count > MaximumPathsPerStep) problems.Add(name + " has more than " + MaximumPathsPerStep + " entries");
        return values;
    }

    private static List<string> ReadStringArray(JsonElement node, string where, List<string> problems)
    {
        var values = new List<string>();
        if (node.ValueKind == JsonValueKind.String)
        {
            values.Add(node.GetString() ?? "");
            return values;
        }
        if (node.ValueKind != JsonValueKind.Array)
        {
            problems.Add(where + " must be a string or an array of strings");
            return values;
        }
        foreach (var element in node.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                problems.Add(where + " must contain only strings");
                continue;
            }
            values.Add(element.GetString() ?? "");
        }
        return values;
    }
}
