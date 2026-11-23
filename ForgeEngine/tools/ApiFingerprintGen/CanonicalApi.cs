using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.CodeAnalysis;

/// <summary>Canonical v1 reads metadata symbols only. Ordered positions stay ordered; unordered declarations
/// and constraint/interface sets sort by their own canonical JSON using ordinal comparison.</summary>
internal sealed partial class CanonicalApi(IAssemblySymbol assembly, string assemblyPath, Compilation compilation)
{
    private IAssemblySymbol MetadataAssembly => assembly;
    private ImmutableArray<IAssemblySymbol> ReferenceAssemblies => compilation.SourceModule.ReferencedAssemblySymbols;
    private Compilation MetadataCompilation => compilation;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly List<SortedDictionary<string, object?>> rows = new();
    private static SortedDictionary<string, object?> Row(params (string Key, object? Value)[] values)
        => new(values.ToDictionary(x => x.Key, x => x.Value), StringComparer.Ordinal);
    private static string Encode(object? value) => JsonSerializer.Serialize(value, Json);
    private static object[] Sorted(IEnumerable<object> values) => values.OrderBy(Encode, StringComparer.Ordinal).ToArray();
    private static bool Access(Accessibility access) => access is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;
    private static bool Visible(ISymbol symbol) => Access(symbol.DeclaredAccessibility)
        && (symbol.ContainingType == null || Visible(symbol.ContainingType));
    private static bool InterfaceVisible(ISymbol member) => Visible(member.ContainingType);
    private static bool Exposed(IMethodSymbol method) => Visible(method) || method.ExplicitInterfaceImplementations.Any(InterfaceVisible);
    private static bool Exposed(IPropertySymbol property) => Visible(property) || property.ExplicitInterfaceImplementations.Any(InterfaceVisible);
    private static bool Exposed(IEventSymbol item) => Visible(item) || item.ExplicitInterfaceImplementations.Any(InterfaceVisible);
    private static string Name(INamedTypeSymbol type) => type.ContainingType != null ? Name(type.ContainingType) + "+" + type.MetadataName
        : (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".") + type.MetadataName;
    private object AssemblyIdentity(IAssemblySymbol owner) => SymbolEqualityComparer.Default.Equals(owner, assembly) ? "self"
        : Row(("name", owner.Identity.Name), ("publicKeyToken", Convert.ToHexString(owner.Identity.PublicKeyToken.ToArray()).ToLowerInvariant()));
    private object Identity(INamedTypeSymbol type) => Row(("assembly", AssemblyIdentity(type.ContainingAssembly)), ("name", Name(type.OriginalDefinition)));
    private static string Annotation(ITypeSymbol type) => type.IsReferenceType || type is ITypeParameterSymbol
        ? type.NullableAnnotation.ToString() : "value";
    private object Type(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Error) throw new InvalidDataException("Unresolved signature type: " + type.ToDisplayString());
        return type switch
        {
            IDynamicTypeSymbol => Row(("kind", "dynamic"), ("nullable", Annotation(type))),
            IArrayTypeSymbol value => Row(("kind", "array"), ("rank", value.Rank), ("szArray", value.IsSZArray), ("element", Type(value.ElementType)), ("nullable", Annotation(type)), ("modifiers", Modifiers(value.CustomModifiers))),
            IPointerTypeSymbol value => Row(("kind", "pointer"), ("element", Type(value.PointedAtType)), ("modifiers", Modifiers(value.CustomModifiers))),
            IFunctionPointerTypeSymbol value => Row(("kind", "function-pointer"), ("signature", MethodSignature(value.Signature))),
            ITypeParameterSymbol value => Row(("kind", "parameter"), ("owner", value.TypeParameterKind.ToString()), ("typeOwner", value.DeclaringType == null ? null : Name(value.DeclaringType)), ("ordinal", value.Ordinal), ("nullable", Annotation(value))),
            INamedTypeSymbol value when value.IsTupleType => Row(("kind", "tuple"), ("identity", Identity(value.TupleUnderlyingType!)),
                ("elements", value.TupleElements.Select(field => Row(("name", field.Name), ("type", Type(field.Type)))).ToArray())),
            INamedTypeSymbol value => Row(("kind", "named"), ("identity", Identity(value)), ("containingType", value.ContainingType == null ? null : Type(value.ContainingType)),
                ("arguments", value.TypeArguments.Select(Type).ToArray()), ("nullable", Annotation(value)), ("nativeInteger", value.IsNativeIntegerType)),
            _ => throw new InvalidDataException("Unsupported signature type kind: " + type.TypeKind)
        };
    }
    private object[] Modifiers(ImmutableArray<CustomModifier> modifiers) => modifiers.Select(value => (object)Row(("optional", value.IsOptional), ("type", Type(value.Modifier)))).ToArray();
    private static object Literal(object? value) => value switch
    {
        null => Row(("kind", "null")),
        bool x => Row(("kind", "boolean"), ("value", x)),
        string x => Row(("kind", "string"), ("value", x)),
        char x => Row(("kind", "char"), ("utf16", ((int)x).ToString("X4", CultureInfo.InvariantCulture))),
        float x => Row(("kind", "float32"), ("bits", unchecked((uint)BitConverter.SingleToInt32Bits(x)).ToString("X8", CultureInfo.InvariantCulture))),
        double x => Row(("kind", "float64"), ("bits", unchecked((ulong)BitConverter.DoubleToInt64Bits(x)).ToString("X16", CultureInfo.InvariantCulture))),
        decimal x => Row(("kind", "decimal"), ("words", decimal.GetBits(x).Select(word => unchecked((uint)word).ToString("X8", CultureInfo.InvariantCulture)).ToArray())),
        DateTime x => Row(("kind", "datetime"), ("ticks", x.Ticks.ToString(CultureInfo.InvariantCulture)), ("dateKind", x.Kind.ToString())),
        sbyte or byte or short or ushort or int or uint or long or ulong => Row(("kind", value.GetType().FullName), ("value", ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture))),
        _ => throw new InvalidDataException("Unsupported signature constant: " + value.GetType().FullName)
    };
    private static bool Attribute(ISymbol value, string name) => value.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == name);
    private static void Supported(ISymbol value)
    {
        if (value.HasUnsupportedMetadata) throw new InvalidDataException("Unsupported public signature metadata: " + value.ToDisplayString());
    }
    private object Parameter(IParameterSymbol value) => Row(("name", value.Name), ("ordinal", value.Ordinal), ("type", Type(value.Type)),
        ("attributes", Attributes(value.GetAttributes())), ("refKind", value.RefKind.ToString()), ("scoped", value.ScopedKind.ToString()), ("unscopedRef", Attribute(value, "System.Diagnostics.CodeAnalysis.UnscopedRefAttribute")), ("params", value.IsParams), ("optional", value.IsOptional),
        ("hasDefault", value.HasExplicitDefaultValue), ("default", value.HasExplicitDefaultValue ? Literal(value.ExplicitDefaultValue) : null),
        ("modifiers", Modifiers(value.CustomModifiers)), ("refModifiers", Modifiers(value.RefCustomModifiers)));
    private object Generic(ITypeParameterSymbol value) => Row(("name", value.Name), ("ordinal", value.Ordinal), ("variance", value.Variance.ToString()),
        ("attributes", Attributes(value.GetAttributes())), ("constructor", value.HasConstructorConstraint), ("reference", value.HasReferenceTypeConstraint), ("referenceNullable", value.ReferenceTypeConstraintNullableAnnotation.ToString()),
        ("value", value.HasValueTypeConstraint), ("unmanaged", value.HasUnmanagedTypeConstraint), ("notNull", value.HasNotNullConstraint),
        ("constraints", Sorted(value.ConstraintTypes.Select(Type))));
    private object MethodSignature(IMethodSymbol value) => Row(("return", Type(value.ReturnType)), ("refReturn", value.RefKind.ToString()),
        ("returnAttributes", Attributes(value.GetReturnTypeAttributes())), ("returnModifiers", Modifiers(value.ReturnTypeCustomModifiers)), ("refModifiers", Modifiers(value.RefCustomModifiers)),
        ("parameters", value.Parameters.Select(Parameter).ToArray()), ("typeParameters", value.TypeParameters.Select(Generic).ToArray()),
        ("callingConvention", value.CallingConvention.ToString()), ("unmanagedConventions", Sorted(value.UnmanagedCallingConventionTypes.Select(Type))),
        ("vararg", value.IsVararg));
    private object Slot(ISymbol value) => value switch
    {
        IMethodSymbol method => Row(("owner", Type(method.ContainingType)), ("name", method.MetadataName), ("signature", MethodSignature(method))),
        IPropertySymbol property => Row(("owner", Type(property.ContainingType)), ("name", property.MetadataName), ("type", Type(property.Type)), ("parameters", property.Parameters.Select(Parameter).ToArray())),
        IEventSymbol item => Row(("owner", Type(item.ContainingType)), ("name", item.MetadataName), ("type", Type(item.Type))),
        _ => throw new InvalidDataException("Unsupported explicit interface slot: " + value.Kind)
    };
    private object? Accessor(IMethodSymbol? value) => value == null || !Exposed(value) ? null : Row(("access", value.DeclaredAccessibility.ToString()),
        ("attributes", Attributes(value.GetAttributes())), ("static", value.IsStatic), ("abstract", value.IsAbstract), ("virtual", value.IsVirtual), ("override", value.IsOverride), ("sealed", value.IsSealed),
        ("readonly", value.IsReadOnly), ("init", value.IsInitOnly), ("unscopedRef", Attribute(value, "System.Diagnostics.CodeAnalysis.UnscopedRefAttribute")), ("signature", MethodSignature(value)),
        ("explicitSlots", Sorted(value.ExplicitInterfaceImplementations.Where(InterfaceVisible).Select(Slot))));
    private void AddType(INamedTypeSymbol type)
    {
        if (!Visible(type)) return;
        Supported(type);
        foreach (var item in type.Interfaces) _ = Type(item);
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Enum or TypeKind.Delegate))
            throw new InvalidDataException("Unsupported public type kind: " + type.TypeKind);
        rows.Add(Row(("kind", "type"), ("identity", Identity(type)), ("typeKind", type.TypeKind.ToString()), ("access", type.DeclaredAccessibility.ToString()),
            ("attributes", Attributes(type.GetAttributes())), ("layout", Layout(type)), ("static", type.IsStatic), ("abstract", type.IsAbstract), ("sealed", type.IsSealed), ("readonly", type.IsReadOnly), ("refLike", type.IsRefLikeType),
            ("record", type.IsRecord), ("base", type.BaseType == null ? null : Type(type.BaseType)),
            ("interfaces", Sorted(type.Interfaces.Where(Visible).Select(Type))), ("parameters", type.TypeParameters.Select(Generic).ToArray()),
            ("enumUnderlying", type.EnumUnderlyingType == null ? null : Type(type.EnumUnderlyingType))));
        foreach (var member in type.GetMembers())
        {
            if (Visible(member) || member is IMethodSymbol exposedMethod && Exposed(exposedMethod) || member is IPropertySymbol exposedProperty && Exposed(exposedProperty) || member is IEventSymbol exposedEvent && Exposed(exposedEvent)) Supported(member);
            if (member is INamedTypeSymbol nested) { AddType(nested); continue; }
            if (member is IMethodSymbol method && Exposed(method) && method.AssociatedSymbol == null)
                rows.Add(Row(("kind", "method"), ("owner", Identity(type)), ("name", method.MetadataName), ("methodKind", method.MethodKind.ToString()),
                    ("attributes", Attributes(method.GetAttributes())), ("access", method.DeclaredAccessibility.ToString()), ("static", method.IsStatic), ("abstract", method.IsAbstract), ("virtual", method.IsVirtual),
                    ("override", method.IsOverride), ("sealed", method.IsSealed), ("readonly", method.IsReadOnly), ("extension", method.IsExtensionMethod), ("setsRequiredMembers", Attribute(method, "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute")), ("unscopedRef", Attribute(method, "System.Diagnostics.CodeAnalysis.UnscopedRefAttribute")),
                    ("signature", MethodSignature(method)), ("explicitSlots", Sorted(method.ExplicitInterfaceImplementations.Where(InterfaceVisible).Select(Slot)))));
            else if (member is IPropertySymbol property && Exposed(property))
                rows.Add(Row(("kind", "property"), ("owner", Identity(type)), ("name", property.MetadataName), ("type", Type(property.Type)),
                    ("refKind", property.RefKind.ToString()), ("modifiers", Modifiers(property.TypeCustomModifiers)), ("refModifiers", Modifiers(property.RefCustomModifiers)),
                    ("attributes", Attributes(property.GetAttributes())), ("indexer", property.IsIndexer), ("required", property.IsRequired), ("parameters", property.Parameters.Select(Parameter).ToArray()),
                    ("get", Accessor(property.GetMethod)), ("set", Accessor(property.SetMethod)),
                    ("explicitSlots", Sorted(property.ExplicitInterfaceImplementations.Where(InterfaceVisible).Select(Slot)))));
            else if (member is IEventSymbol item && Exposed(item))
                rows.Add(Row(("kind", "event"), ("owner", Identity(type)), ("name", item.MetadataName), ("type", Type(item.Type)),
                    ("attributes", Attributes(item.GetAttributes())), ("add", Accessor(item.AddMethod)), ("remove", Accessor(item.RemoveMethod)), ("raise", Accessor(item.RaiseMethod)),
                    ("explicitSlots", Sorted(item.ExplicitInterfaceImplementations.Where(InterfaceVisible).Select(Slot)))));
            else if (member is IFieldSymbol field && Visible(field))
            {
                if (field.IsFixedSizeBuffer) throw new InvalidDataException("Public fixed buffers require an explicit canonical format extension: " + field.ToDisplayString());
                if (type.TypeKind == TypeKind.Enum && !field.HasConstantValue) continue;
                bool self = Name(type) == "ForgeEngine.Framework.RuntimeApi" && field.MetadataName == "Fingerprint";
                rows.Add(Row(("kind", "field"), ("owner", Identity(type)), ("name", field.MetadataName), ("type", Type(field.Type)),
                    ("attributes", Attributes(field.GetAttributes())), ("access", field.DeclaredAccessibility.ToString()), ("static", field.IsStatic), ("readonly", field.IsReadOnly), ("volatile", field.IsVolatile),
                    ("required", field.IsRequired), ("const", field.IsConst), ("hasConstant", field.HasConstantValue), ("constant", self ? "fingerprint-self-reference" : field.HasConstantValue ? Literal(field.ConstantValue) : null),
                    ("modifiers", Modifiers(field.CustomModifiers)), ("refModifiers", Modifiers(field.RefCustomModifiers)), ("refKind", field.RefKind.ToString())));
            }
            else if (Visible(member) && member is not (IMethodSymbol or IPropertySymbol or IEventSymbol or IFieldSymbol))
                throw new InvalidDataException("Unsupported public symbol: " + member.Kind);
        }
    }
    private void Namespace(INamespaceSymbol value)
    {
        foreach (var child in value.GetNamespaceMembers()) Namespace(child);
        foreach (var type in value.GetTypeMembers()) AddType(type);
    }
    public string Read()
    {
        ReadMetadataContracts();
        Namespace(assembly.GlobalNamespace);
        return Encode(Row(("format", "forge-engine-public-api/1"), ("declarations", Sorted(rows)))) + "\n";
    }
}
