using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

internal sealed partial class CanonicalApi
{
    private readonly HashSet<ISymbol> activeLayouts = new(SymbolEqualityComparer.Default);
    private object PhysicalType(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Error) throw new InvalidDataException("Unresolved physical layout type.");
        // Reference annotations, tuple labels and dynamic transport do not change storage or GC category.
        if (type is IDynamicTypeSymbol || type.SpecialType == SpecialType.System_Object) return "object-reference";
        if (type.SpecialType == SpecialType.System_String) return "string-reference";
        if (type is IArrayTypeSymbol array) return Row(("kind", "array-reference"), ("rank", array.Rank), ("szArray", array.IsSZArray), ("element", PhysicalType(array.ElementType)));
        if (type.IsReferenceType && type is INamedTypeSymbol reference)
        {
            var owned = SymbolEqualityComparer.Default.Equals(reference.ContainingAssembly, assembly);
            return Row(("kind", "reference"), ("category", reference.TypeKind.ToString()),
                ("identity", owned && !Visible(reference) ? "hidden-reference" : Identity(reference)),
                ("arguments", reference.TypeArguments.Select(PhysicalType).ToArray()),
                ("layout", owned ? Layout(reference) : null));
        }
        if (type is IPointerTypeSymbol or IFunctionPointerTypeSymbol) return "unmanaged-pointer";
        if (type is ITypeParameterSymbol parameter) return Row(("kind", "generic-storage"), ("owner", parameter.TypeParameterKind.ToString()), ("ordinal", parameter.Ordinal),
            ("typeOwner", parameter.DeclaringType == null ? null : Name(parameter.DeclaringType)));
        if (type is INamedTypeSymbol named)
        {
            if (named.TypeKind == TypeKind.Enum) return PhysicalType(named.EnumUnderlyingType!);
            if (named.IsTupleType) named = named.TupleUnderlyingType!;
            return Row(("identity", SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, assembly) && !Visible(named) ? "hidden-value-layout" : Identity(named)), ("arguments", named.TypeArguments.Select(PhysicalType).ToArray()),
                ("layout", named.IsValueType && SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, assembly) ? Layout(named) : null));
        }
        throw new InvalidDataException("Unsupported physical layout type: " + type.TypeKind);
    }
    private object Layout(INamedTypeSymbol symbol)
    {
        if (activeLayouts.Count >= 64) throw new InvalidDataException("Physical value layout nesting exceeds the supported bound of 64.");
        if (!activeLayouts.Add(symbol)) throw new InvalidDataException("Cyclic physical value layout: " + Name(symbol));
        try { return ReadLayout(symbol); } finally { activeLayouts.Remove(symbol); }
    }
    private object ReadLayout(INamedTypeSymbol symbol)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        string MetadataName(TypeDefinitionHandle handle)
        {
            var definition = reader.GetTypeDefinition(handle); var parent = definition.GetDeclaringType();
            var name = reader.GetString(definition.Name);
            if (!parent.IsNil) return MetadataName(parent) + "+" + name;
            var ns = reader.GetString(definition.Namespace); return ns.Length == 0 ? name : ns + "." + name;
        }
        var handle = reader.TypeDefinitions.Single(h => MetadataName(h) == Name(symbol));
        var type = reader.GetTypeDefinition(handle); var layout = type.GetLayout();
        var kind = type.Attributes & TypeAttributes.LayoutMask;
        bool physical = symbol.TypeKind == TypeKind.Struct || kind != TypeAttributes.AutoLayout;
        object? baseLayout = null;
        if (physical && symbol.TypeKind == TypeKind.Class && symbol.BaseType is { } parent && parent.SpecialType != SpecialType.System_Object)
        {
            if (parent.TypeKind == TypeKind.Error) throw new InvalidDataException("Unresolved physical base layout: " + Name(symbol));
            if (!SymbolEqualityComparer.Default.Equals(parent.ContainingAssembly, assembly))
                throw new InvalidDataException("External physical base layout requires an explicit canonical decoder: " + Name(symbol));
            baseLayout = PhysicalType(parent);
        }
        var fields = new List<object>();
        if (physical)
            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & FieldAttributes.Static) != 0) continue;
                var name = reader.GetString(field.Name);
                var fieldSymbol = symbol.GetMembers(name).OfType<IFieldSymbol>().SingleOrDefault()
                    ?? throw new InvalidDataException("Unable to decode physical layout field: " + Name(symbol) + "." + name);
                Supported(fieldSymbol);
                if (fieldSymbol.IsFixedSizeBuffer) throw new InvalidDataException("Fixed physical layout buffers require an explicit canonical decoder: " + Name(symbol));
                // Preserve externally visible field-to-storage identity; private names are implementation detail.
                fields.Add(Row(("publicName", Visible(fieldSymbol) ? name : null), ("type", PhysicalType(fieldSymbol.Type)), ("offset", field.GetOffset()), ("refKind", fieldSymbol.RefKind.ToString()),
                    ("modifiers", Modifiers(fieldSymbol.CustomModifiers)), ("refModifiers", Modifiers(fieldSymbol.RefCustomModifiers)),
                    ("marshal", PhysicalMarshal(reader, fieldHandle))));
            }
        return Row(("kind", kind.ToString()), ("baseLayout", baseLayout), ("pack", layout.PackingSize), ("size", layout.Size),
            ("charset", (type.Attributes & TypeAttributes.StringFormatMask).ToString()), ("fields", kind == TypeAttributes.ExplicitLayout ? Sorted(fields) : fields.ToArray()));
    }
}
