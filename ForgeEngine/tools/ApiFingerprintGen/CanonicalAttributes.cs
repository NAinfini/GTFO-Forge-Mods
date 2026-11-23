using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

internal sealed partial class CanonicalApi
{
    // These transport encodings are represented by semantic symbol fields, or describe only
    // compiler-generated implementation/debugging. Never ignore an entire attribute namespace.
    private static readonly HashSet<string> TransportAttributes = new(StringComparer.Ordinal)
    {
        "System.Runtime.CompilerServices.NullableAttribute",
        "System.Runtime.CompilerServices.NullableContextAttribute",
        "System.Runtime.CompilerServices.NullablePublicOnlyAttribute",
        "System.Runtime.CompilerServices.DynamicAttribute",
        "System.Runtime.CompilerServices.TupleElementNamesAttribute",
        "System.Runtime.CompilerServices.NativeIntegerAttribute",
        "System.Runtime.CompilerServices.IsReadOnlyAttribute",
        "System.Runtime.CompilerServices.IsByRefLikeAttribute",
        "System.Runtime.CompilerServices.IsUnmanagedAttribute",
        "System.Runtime.CompilerServices.ExtensionAttribute",
        "System.ParamArrayAttribute",
        "System.Runtime.CompilerServices.ScopedRefAttribute",
    };
    private static readonly HashSet<string> ImplementationAttributes = new(StringComparer.Ordinal)
    {
        "System.Runtime.CompilerServices.CompilerGeneratedAttribute",
        "System.Runtime.CompilerServices.AsyncStateMachineAttribute",
        "System.Runtime.CompilerServices.IteratorStateMachineAttribute",
        "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute",
        "System.Diagnostics.DebuggerHiddenAttribute",
        "System.Diagnostics.DebuggerStepThroughAttribute",
        "System.Diagnostics.DebuggerNonUserCodeAttribute",
        "System.Diagnostics.DebuggerDisplayAttribute",
        "System.Diagnostics.DebuggerTypeProxyAttribute",
        "System.Diagnostics.DebuggerBrowsableAttribute"
    };
    private object AttributeArgument(TypedConstant value)
    {
        if (value.Kind == TypedConstantKind.Error || value.Type == null)
            throw new InvalidDataException("Unresolved public contract attribute argument.");
        object? payload = value.IsNull ? null : value.Kind switch
        {
            TypedConstantKind.Array => value.Values.Select(AttributeArgument).ToArray(),
            TypedConstantKind.Type => value.Value is ITypeSymbol type ? Type(type) : throw new InvalidDataException("Invalid attribute type argument."),
            TypedConstantKind.Primitive or TypedConstantKind.Enum => Literal(value.Value),
            _ => throw new InvalidDataException("Unsupported public contract attribute argument: " + value.Kind)
        };
        return Row(("kind", value.Kind.ToString()), ("type", Type(value.Type)), ("null", value.IsNull), ("value", payload));
    }
    private object[] Attributes(ImmutableArray<AttributeData> attributes)
    {
        var result = new List<object>();
        foreach (var attribute in attributes)
        {
            var type = attribute.AttributeClass ?? throw new InvalidDataException("Unresolved public contract attribute type.");
            if (TransportAttributes.Contains(Name(type)) || ImplementationAttributes.Contains(Name(type))) continue;
            Supported(type);
            var constructor = attribute.AttributeConstructor ?? throw new InvalidDataException("Unresolved public contract attribute constructor: " + Name(type));
            Supported(constructor);
            if (attribute.ConstructorArguments.Length != constructor.Parameters.Length) throw new InvalidDataException("Malformed public contract attribute arguments: " + Name(type));
            // Preserve unknown user markers conservatively too. Constructor parameter types disambiguate overloads.
            result.Add(Row(("type", Type(type)), ("constructor", constructor.Parameters.Select(p => Type(p.Type)).ToArray()),
                ("arguments", attribute.ConstructorArguments.Select(AttributeArgument).ToArray()),
                ("named", attribute.NamedArguments.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => Row(("name", x.Key), ("value", AttributeArgument(x.Value)))).ToArray())));
        }
        return Sorted(result);
    }
}
