using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>Request-private borrowed references. Cached wrappers are strong IL2CPP GC roots, used only for
/// reference identity; every Unity getter uses a fresh object reached from the entity owner's current root.
/// This does not pin Unity's C++ lifetime or provide a mutation version. The result is always an interval.</summary>
internal sealed class RecUnityProjectionAdapter : IRecProjectionAdapter
{
    internal static readonly IReadOnlyList<string> AllowedFields = Array.AsReadOnly(new[] { "name", "activeSelf", "layer" });
    internal static readonly RecProjectionOperation RootOperation = new("node", "0", "", 0, 0);
    private readonly string[] fields;
    private readonly bool hierarchy, materials, shaderProperties;
    private readonly Dictionary<string, Borrowed> borrowed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string? Name, string? Type)> properties = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CaptureRow> pendingRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PropertyRead> pendingProperties = new(StringComparer.Ordinal);
    private readonly List<RecProjectionOperation> validation = new();
    private readonly HashSet<string> emittedShaders = new(StringComparer.Ordinal);
    private long sequence;
    private bool disposed;

    private sealed record Borrowed(string Token, Il2CppObjectBase? Wrapper);
    private sealed class PropertyRead
    {
        internal PropertyRead(CaptureRow row) { Row = row; }
        internal CaptureRow Row { get; }
        internal string? Name, Type;
        internal bool? Present;
    }
    private sealed class Changed : Exception { }

    internal RecUnityProjectionAdapter(IReadOnlyList<string> fields, bool hierarchy, bool materials, bool shaderProperties)
    {
        var approved = new List<string>();
        foreach (var field in fields)
        {
            if (field is not ("name" or "activeSelf" or "layer")) throw new ArgumentException("projection-field", nameof(fields));
            if (!approved.Contains(field)) approved.Add(field);
        }
        // Hierarchy rows have their own fixed structural schema, including these three approved scalars.
        if (hierarchy) foreach (var field in AllowedFields) if (!approved.Contains(field)) approved.Add(field);
        this.fields = approved.ToArray(); this.hierarchy = hierarchy; this.materials = materials;
        this.shaderProperties = shaderProperties;
    }

    /// <summary>A target is one player's native life (<see cref="NativeLife"/>): the token is <see cref="PlayerToken"/> plus its
    /// address, the generation its Unity instance id and the world this package's own epoch.</summary>
    internal const string Owner = "native-life", PlayerToken = "gtfo.player:";

    internal static bool SupportsBinding(TargetBinding binding)
        => binding.Owner == Owner && binding.WorldEpoch >= 0 && binding.Token.StartsWith(PlayerToken, StringComparison.Ordinal);

    public object? Resolve(TargetBinding binding)
    {
        if (disposed || !SupportsBinding(binding)
            || !long.TryParse(binding.Token.AsSpan(PlayerToken.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pointer)) return null;
        var agents = Player.PlayerManager.PlayerAgentsInLevel;
        Player.PlayerAgent? root = null;
        for (var i = 0; agents != null && i < agents.Count; i++)
            if (agents[i] is { } agent && !agent.WasCollected && agent.Pointer.ToInt64() == pointer) root = agent;
        if (root == null || NativeLife.Of(root) is not { } life || life.Instance != binding.Generation
            || life.WorldEpoch != binding.WorldEpoch) return null;
        var wrapper = (Il2CppObjectBase)root;
        if (borrowed.TryGetValue("owner", out var old) && !Same(old.Wrapper, wrapper)) return null;
        Remember("owner", wrapper);
        return root;
    }

    public bool IsCurrent(TargetBinding binding, object root, RecProjectionOperation operation)
    {
        if (disposed || !SupportsBinding(binding)) return false;
        try
        {
            var fresh = Resolve(binding);
            if (fresh is not Il2CppObjectBase current || root is not Il2CppObjectBase previous || current.Pointer != previous.Pointer) return false;
            if (operation.Kind == "validate")
            {
                if (operation.Index >= validation.Count) return true;
                Check(current, validation[operation.Index]);
            }
            else Check(current, operation);
            return true;
        }
        catch (Changed) { return false; }
        catch (RecObservationTerminated) { throw; }
        catch (Exception error) { throw new RecObservationTerminated("error", "projection validation: " + error.GetType().Name); }
    }

    public RecProjectionStep Read(object root, RecProjectionOperation operation)
    {
        if (disposed) return new(null, Array.Empty<RecProjectionOperation>(), "target-replaced");
        try
        {
            if (operation.Kind == "validate")
            {
                if (operation.Index < validation.Count) Check(root, validation[operation.Index]);
                return new(null, operation.Index + 1 < validation.Count
                    ? new[] { operation with { Index = operation.Index + 1 } } : Array.Empty<RecProjectionOperation>());
            }
            var node = Node(root, operation.Path);
            switch (operation.Kind)
            {
                case "node":
                    {
                        var token = Remember(NodeKey(operation.Path), node);
                        validation.Add(operation with { ValidateOnly = true, Token = token });
                        var row = Row("node", operation, "").Value("parent", operation.Parent.Length == 0
                            ? RecProjectionValue.Null("reference") : RecProjectionValue.Reference(operation.Parent))
                            .Value("childIndex", RecProjectionValue.Number(operation.Index))
                            .Value("reference", RecProjectionValue.Reference(token));
                        if (fields.Length == 0) return AfterNode(row, operation with { Token = token });
                        pendingRows.Add(NodeKey(operation.Path), row);
                        return Step(null, operation with { Kind = "node-scalar", Index = 0, Token = token });
                    }
                case "node-scalar":
                    {
                        var key = NodeKey(operation.Path);
                        var row = pendingRows[key];
                        var field = fields[operation.Index];
                        ReadField(row, field, () => field switch
                        {
                            "name" => RecProjectionValue.Text(Native(() => node.name) ?? ""),
                            "activeSelf" => RecProjectionValue.Boolean(Native(() => node.activeSelf)),
                            _ => RecProjectionValue.Number(Native(() => node.layer))
                        });
                        if (operation.Index + 1 < fields.Length) return Step(null, operation with { Index = operation.Index + 1 });
                        pendingRows.Remove(key);
                        return AfterNode(row, operation);
                    }
                case "components":
                    {
                        var components = Native(() => node.GetComponents<Component>());
                        if (components is null) throw new InvalidOperationException("components-returned-null");
                        var count = checked((int)Native(() => components.Length));
                        if (operation.Index == -1)
                        {
                            Count(operation, count);
                            return Step(Row("components", operation, "").Value("count", RecProjectionValue.Count(count)),
                                operation with { Index = 0 });
                        }
                        RequireCount(operation, count);
                        if (operation.Index >= count) return Step(null, operation with { Kind = "children", Index = -1 });
                        var component = Native(() => components[operation.Index]);
                        var token = Remember(ComponentKey(operation), component);
                        validation.Add(operation with { ValidateOnly = true, Token = token });
                        var row = Row("component", operation, "").Value("componentIndex", RecProjectionValue.Number(operation.Index))
                            .Value("reference", component is null ? RecProjectionValue.Null("reference") : RecProjectionValue.Reference(token));
                        if (component is null) return Step(row, operation with { Index = operation.Index + 1 });
                        pendingRows.Add(ComponentKey(operation), row);
                        return Step(null, operation with { Kind = "component-type", Token = token });
                    }
                case "component-type":
                    {
                        var component = ComponentAt(node, operation);
                        var key = ComponentKey(operation);
                        var row = pendingRows[key];
                        ReadField(row, "type", () =>
                        {
                            var type = Native(() => component.GetIl2CppType());
                            return RecProjectionValue.Text(Native(() => type.FullName) ?? "");
                        });
                        pendingRows.Remove(key);
                        var continuation = operation with { Kind = "components", Index = operation.Index + 1 };
                        return materials && Native(() => component.TryCast<Renderer>()) is not null
                            ? Step(row, operation with { Kind = "materials", Parent = Index(operation.Index), Index = -1 }, continuation)
                            : Step(row, continuation);
                    }
                case "materials":
                    {
                        var renderer = RendererAt(node, operation);
                        var slots = Native(() => renderer.sharedMaterials);
                        if (slots is null) throw new InvalidOperationException("materials-returned-null");
                        var count = checked((int)Native(() => slots.Length));
                        if (operation.Index == -1)
                        {
                            Count(operation, count);
                            return Step(Row("materials", operation, "").Value("componentIndex", RecProjectionValue.Number(ComponentIndex(operation)))
                                .Value("count", RecProjectionValue.Count(count)), operation with { Index = 0 });
                        }
                        RequireCount(operation, count);
                        if (operation.Index >= count) return Step(null);
                        var material = Native(() => slots[operation.Index]);
                        var token = Remember(MaterialKey(operation), material);
                        validation.Add(operation with { ValidateOnly = true, Token = token });
                        var row = Row("material-slot", operation, "").Value("componentIndex", RecProjectionValue.Number(ComponentIndex(operation)))
                            .Value("slotIndex", RecProjectionValue.Number(operation.Index))
                            .Value("material", material is null ? RecProjectionValue.Null("reference") : RecProjectionValue.Reference(token));
                        return material is not null && shaderProperties
                            ? Step(row, operation with { Kind = "shader", Token = token }, operation with { Index = operation.Index + 1 })
                            : Step(row, operation with { Index = operation.Index + 1 });
                    }
                case "shader":
                    {
                        var material = MaterialAt(node, operation);
                        var shader = Native(() => material.shader);
                        var token = Remember(ShaderKey(operation), shader);
                        validation.Add(operation with { ValidateOnly = true, Token = token });
                        var row = Row("material-shader", operation, "").Value("material", RecProjectionValue.Reference(Token(MaterialKey(operation))))
                            .Value("shader", shader is null ? RecProjectionValue.Null("reference") : RecProjectionValue.Reference(token));
                        if (shader is null) return Step(row);
                        return Step(row, operation with { Kind = "shader-count", Token = token });
                    }
                case "shader-count":
                    {
                        var shader = ShaderAt(node, operation);
                        var count = Native(() => shader.GetPropertyCount());
                        if (count < 0) throw new InvalidOperationException("negative-count");
                        Count(operation, count);
                        var row = emittedShaders.Add(operation.Token)
                            ? new CaptureRow("shader", operation.Token, "").Value("reference", RecProjectionValue.Reference(operation.Token))
                                .Value("count", RecProjectionValue.Count(count)) : null;
                        return Step(row, operation with { Kind = "property", Token = Index(operation.Index), Index = 0 });
                    }
                case "property":
                    {
                        var slot = operation with { Index = int.Parse(operation.Token, CultureInfo.InvariantCulture) };
                        var count = counts[CountKey(slot with { Kind = "shader-count" })];
                        if (operation.Index >= count) return Step(null);
                        var row = Row("shader-property", operation, "").Value("componentIndex", RecProjectionValue.Number(ComponentIndex(operation)))
                            .Value("slotIndex", RecProjectionValue.Number(slot.Index)).Value("propertyIndex", RecProjectionValue.Number(operation.Index));
                        pendingProperties.Add(PropertyKey(operation), new(row));
                        return Step(null, operation with { Kind = "property-name" });
                    }
                case "property-name":
                    {
                        var state = pendingProperties[PropertyKey(operation)];
                        var slot = PropertySlot(operation);
                        var shader = ShaderAt(node, slot);
                        ReadField(state.Row, "name", () =>
                        {
                            state.Name = Native(() => shader.GetPropertyName(operation.Index)) ?? throw new InvalidOperationException("property-name-returned-null");
                            state.Row.Name = state.Name;
                            return RecProjectionValue.Text(state.Name);
                        });
                        properties[PropertyKey(operation)] = (state.Name, null);
                        validation.Add(operation with { ValidateOnly = true });
                        return Step(null, operation with { Kind = "property-type" });
                    }
                case "property-type":
                    {
                        var state = pendingProperties[PropertyKey(operation)];
                        var shader = ShaderAt(node, PropertySlot(operation));
                        ReadField(state.Row, "propertyType", () =>
                        {
                            state.Type = Native(() => shader.GetPropertyType(operation.Index)).ToString();
                            return RecProjectionValue.Text(state.Type);
                        });
                        properties[PropertyKey(operation)] = (state.Name, state.Type);
                        return Step(null, operation with { Kind = "property-presence" });
                    }
                case "property-presence":
                    {
                        var state = pendingProperties[PropertyKey(operation)];
                        if (state.Name is null) state.Row.Value("presence", RecProjectionValue.Error("boolean", "name-unavailable"));
                        else
                        {
                            var material = MaterialAt(node, PropertySlot(operation));
                            ReadField(state.Row, "presence", () =>
                            {
                                state.Present = Native(() => material.HasProperty(state.Name));
                                return RecProjectionValue.Boolean(state.Present.Value);
                            });
                        }
                        return Step(null, operation with { Kind = "property-value" });
                    }
                case "property-value":
                    {
                        var key = PropertyKey(operation);
                        var state = pendingProperties[key];
                        if (state.Present == true && state.Type == "Color")
                        {
                            var material = MaterialAt(node, PropertySlot(operation));
                            ReadField(state.Row, "value", () =>
                            {
                                var color = Native(() => material.GetColor(state.Name!));
                                return RecProjectionValue.Color(color.r, color.g, color.b, color.a);
                            });
                        }
                        else state.Row.Value("value", state.Present is null ? RecProjectionValue.Error("color", "presence-unavailable")
                            : state.Present == false ? RecProjectionValue.Null("color")
                            : state.Type is null ? RecProjectionValue.Error("color", "type-unavailable")
                            : RecProjectionValue.Unsupported("number", "getter-not-admitted:" + state.Type));
                        pendingProperties.Remove(key);
                        return Step(state.Row, operation with { Kind = "property", Index = operation.Index + 1 });
                    }
                case "children":
                    {
                        if (!hierarchy) return operation.Path == "0"
                            ? Step(null, new RecProjectionOperation("validate", "0", "", 0, 0, ValidateOnly: true)) : Step(null);
                        var count = ChildCount(node);
                        if (operation.Index == -1)
                        {
                            Count(operation, count);
                            return Step(Row("children", operation, "").Value("count", RecProjectionValue.Count(count)), operation with { Index = 0 });
                        }
                        RequireCount(operation, count);
                        if (operation.Index >= count) return operation.Path == "0"
                            ? Step(null, new RecProjectionOperation("validate", "0", "", 0, 0, ValidateOnly: true)) : Step(null);
                        var childPath = operation.Path + "/" + Index(operation.Index);
                        return Step(null, new RecProjectionOperation("node", childPath, operation.Path, operation.Index, operation.Depth + 1),
                            operation with { Index = operation.Index + 1 });
                    }
                default: throw new InvalidOperationException("operation-not-admitted");
            }
        }
        catch (Changed) { return new(null, Array.Empty<RecProjectionOperation>(), "target-replaced"); }
        catch (RecObservationTerminated) { throw; }
        catch (Exception error) when (operation.Kind is "node-scalar" or "component-type" or "validate" || IsProperty(operation))
        { throw new RecObservationTerminated("error", "projection path: " + error.GetType().Name); }
        catch (Exception error)
        {
            var kind = operation.Kind switch
            {
                "components" when operation.Index >= 0 => "component",
                "materials" when operation.Index >= 0 => "material-slot",
                "shader-count" => "shader",
                _ => operation.Kind
            };
            var field = operation.Index == -1 || operation.Kind == "shader-count" ? "count" : "read";
            var row = Row(kind, operation, "").Value(field, RecProjectionValue.Error("collection", error.GetType().Name));
            if (operation.Kind == "components" && operation.Index >= 0)
                row.Value("componentIndex", RecProjectionValue.Number(operation.Index));
            if (operation.Kind == "materials" && operation.Index >= 0)
                row.Value("componentIndex", RecProjectionValue.Number(ComponentIndex(operation)))
                    .Value("slotIndex", RecProjectionValue.Number(operation.Index));
            // Keep the failed original index, then continue only a collection whose count was acquired successfully.
            if (operation.Kind == "components") return operation.Index < 0
                ? Step(row, operation with { Kind = "children", Index = -1 }) : Step(row, operation with { Index = operation.Index + 1 });
            if (operation.Kind == "materials" && operation.Index >= 0)
                return Step(row, operation with { Index = operation.Index + 1 });
            if (operation.Kind == "children") return operation.Path == "0"
                ? Step(row, new RecProjectionOperation("validate", "0", "", 0, 0, ValidateOnly: true)) : Step(row);
            return Step(row);
        }
    }

    private void Check(object root, RecProjectionOperation op)
    {
        var node = Node(root, op.Path);
        if (op.Kind is "components" or "component-type")
        {
            var collection = op with { Kind = "components" };
            if (!counts.ContainsKey(CountKey(collection))) return;
            var values = Native(() => node.GetComponents<Component>());
            if (values is null) throw new Changed();
            var length = Native(() => values.Length);
            RequireCountIfKnown(collection, checked((int)length));
            if (op.Index >= 0 && op.Index < length) CheckReference(ComponentKey(op), Native(() => values[op.Index]));
        }
        else if (op.Kind is "materials" or "shader" or "shader-count" || IsProperty(op))
        {
            var slot = IsProperty(op) ? PropertySlot(op) : op;
            if (op.Kind == "materials" && !counts.ContainsKey(CountKey(op)))
            { RendererAt(node, slot); return; }
            var renderer = RendererAt(node, slot);
            var slots = Native(() => renderer.sharedMaterials);
            if (slots is null) throw new Changed();
            var length = Native(() => slots.Length);
            RequireCountIfKnown(slot with { Kind = "materials" }, checked((int)length));
            if (slot.Index >= 0 && slot.Index < length)
            {
                var material = Native(() => slots[slot.Index]);
                CheckReference(MaterialKey(slot), material);
                if (op.Kind != "materials")
                {
                    if (material is null) throw new Changed();
                    if (op.Kind == "shader" && !borrowed.ContainsKey(ShaderKey(slot))) return;
                    var shader = Native(() => material.shader);
                    CheckReference(ShaderKey(slot), shader);
                    if (op.Kind == "shader-count" || IsProperty(op))
                    {
                        if (shader is null) throw new Changed();
                        var countOperation = slot with { Kind = "shader-count" };
                        if (counts.ContainsKey(CountKey(countOperation)))
                            RequireCountIfKnown(countOperation, Native(() => shader.GetPropertyCount()));
                        if (IsProperty(op) && properties.TryGetValue(PropertyKey(op), out var old))
                        {
                            if (old.Name is not null && Native(() => shader.GetPropertyName(op.Index)) != old.Name) throw new Changed();
                            if (old.Type is not null && Native(() => shader.GetPropertyType(op.Index)).ToString() != old.Type) throw new Changed();
                        }
                    }
                }
            }
        }
        else if (op.Kind == "children" && counts.ContainsKey(CountKey(op)))
            RequireCountIfKnown(op, ChildCount(node));
    }

    private GameObject Node(object root, string path)
    {
        var node = root switch { GameObject value => value, Component value => Native(() => value.gameObject), _ => throw new Changed() };
        if (node is null) throw new Changed();
        var currentPath = "0";
        CheckReference(NodeKey(currentPath), node);
        var indices = path.Split('/');
        if (indices[0] != "0") throw new Changed();
        for (var i = 1; i < indices.Length; i++)
        {
            var index = int.Parse(indices[i], CultureInfo.InvariantCulture);
            RequireCountIfKnown(new("children", currentPath, "", 0, 0), ChildCount(node));
            var transform = Native(() => node.transform);
            var child = Native(() => transform.GetChild(index));
            node = Native(() => child.gameObject);
            if (node is null) throw new Changed();
            currentPath += "/" + indices[i];
            CheckReference(NodeKey(currentPath), node);
        }
        return node;
    }

    private Renderer RendererAt(GameObject node, RecProjectionOperation op)
    {
        var components = Native(() => node.GetComponents<Component>());
        if (components is null) throw new Changed();
        RequireCountIfKnown(op with { Kind = "components", Parent = "" }, checked((int)Native(() => components.Length)));
        var index = ComponentIndex(op);
        var component = Native(() => components[index]);
        CheckReference(ComponentKey(op with { Index = index }), component);
        return component is null ? throw new Changed() : Native(() => component.TryCast<Renderer>()) ?? throw new Changed();
    }

    private Component ComponentAt(GameObject node, RecProjectionOperation op)
    {
        var components = Native(() => node.GetComponents<Component>());
        if (components is null) throw new Changed();
        RequireCount(op with { Kind = "components" }, checked((int)Native(() => components.Length)));
        var component = Native(() => components[op.Index]);
        CheckReference(ComponentKey(op), component);
        return component ?? throw new Changed();
    }

    private Material MaterialAt(GameObject node, RecProjectionOperation op)
    {
        var renderer = RendererAt(node, op);
        var slots = Native(() => renderer.sharedMaterials);
        if (slots is null) throw new Changed();
        RequireCountIfKnown(op with { Kind = "materials" }, checked((int)Native(() => slots.Length)));
        var material = Native(() => slots[op.Index]); CheckReference(MaterialKey(op), material);
        return material ?? throw new Changed();
    }

    private Shader ShaderAt(GameObject node, RecProjectionOperation op)
    {
        var material = MaterialAt(node, op);
        var shader = Native(() => material.shader); CheckReference(ShaderKey(op), shader);
        return shader ?? throw new Changed();
    }

    private static T Native<T>(Func<T> read) => RecObservation.ReadNative(read);
    private static int ChildCount(GameObject node)
    {
        var transform = Native(() => node.transform);
        return Native(() => transform.childCount);
    }

    private string Remember(string key, Il2CppObjectBase? fresh)
    {
        if (borrowed.TryGetValue(key, out var old)) { if (!Same(old.Wrapper, fresh)) throw new Changed(); return old.Token; }
        // Resource aliases share a token by pinned IL2CPP reference, never by a shader name or raw pointer ticket.
        string? token = null;
        if (fresh is not null) foreach (var entry in borrowed.Values)
            if (entry.Wrapper is not null && entry.Wrapper.Pointer == fresh.Pointer) { token = entry.Token; break; }
        token ??= "borrowed-" + (++sequence).ToString(CultureInfo.InvariantCulture);
        borrowed.Add(key, new(token, fresh)); return token;
    }

    private void CheckReference(string key, Il2CppObjectBase? fresh)
    { if (borrowed.TryGetValue(key, out var old) && !Same(old.Wrapper, fresh)) throw new Changed(); }
    private static bool Same(Il2CppObjectBase? held, Il2CppObjectBase? fresh)
        => held is null ? fresh is null : fresh is not null && !held.WasCollected && !fresh.WasCollected
            && held.Pointer != IntPtr.Zero && held.Pointer == fresh.Pointer;
    private string Token(string key) => borrowed[key].Token;
    private void Count(RecProjectionOperation op, int count)
    { counts.Add(CountKey(op), count); validation.Add(op with { ValidateOnly = true }); }
    private void RequireCount(RecProjectionOperation op, int count)
    { if (!counts.TryGetValue(CountKey(op), out var old) || old != count) throw new Changed(); }
    private void RequireCountIfKnown(RecProjectionOperation op, int count)
    { if (counts.TryGetValue(CountKey(op), out var old) && old != count) throw new Changed(); }
    private static string NodeKey(string path) => "node:" + path;
    private static string ComponentKey(RecProjectionOperation op) => "component:" + op.Path + ":" + Index(op.Index);
    private static string MaterialKey(RecProjectionOperation op) => "material:" + op.Path + ":" + op.Parent + ":" + Index(op.Index);
    private static string ShaderKey(RecProjectionOperation op) => "shader:" + op.Path + ":" + op.Parent + ":" + Index(op.Index);
    private static string PropertyKey(RecProjectionOperation op) => "property:" + op.Path + ":" + op.Parent + ":" + op.Token + ":" + Index(op.Index);
    private static bool IsProperty(RecProjectionOperation op)
        => op.Kind is "property" or "property-name" or "property-type" or "property-presence" or "property-value";
    private static RecProjectionOperation PropertySlot(RecProjectionOperation op)
        => op with { Index = int.Parse(op.Token, CultureInfo.InvariantCulture) };
    private static string CountKey(RecProjectionOperation op) => op.Kind + ":" + op.Path + (op.Kind is "materials" or "shader-count" ? ":" + op.Parent : "")
        + (op.Kind == "shader-count" ? ":" + Index(op.Index) : "");
    private static int ComponentIndex(RecProjectionOperation op) => int.Parse(op.Parent, CultureInfo.InvariantCulture);
    private static string Index(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static CaptureRow Row(string kind, RecProjectionOperation op, string name)
        => new CaptureRow(kind, kind + ":" + op.Path + ":" + op.Parent + ":" + op.Token + ":" + Index(op.Index), name)
            .Value("node", RecProjectionValue.Reference(op.Path)).Value("index", RecProjectionValue.Number(op.Index));
    private static void ReadField(CaptureRow row, string field, Func<RecProjectionValue> read)
    {
        try { row.Value(field, read()); }
        catch (Changed) { throw; }
        catch (RecObservationTerminated) { throw; }
        catch (Exception error)
        {
            row.Value(field, RecProjectionValue.Error(field == "name" || field == "type" || field == "propertyType" ? "string"
                : field == "activeSelf" || field == "presence" ? "boolean" : field == "value" ? "color" : "number", error.GetType().Name));
        }
    }
    private RecProjectionStep AfterNode(CaptureRow row, RecProjectionOperation op)
        => hierarchy || materials ? Step(row, op with { Kind = "components", Index = -1 })
            : Step(row, new RecProjectionOperation("validate", "0", "", 0, 0, ValidateOnly: true));
    private static RecProjectionStep Step(CaptureRow? row, params RecProjectionOperation[] next) => new(row, next);

    public void Dispose()
    {
        disposed = true; borrowed.Clear(); counts.Clear(); properties.Clear(); pendingRows.Clear();
        pendingProperties.Clear(); validation.Clear(); emittedShaders.Clear();
    }
}
