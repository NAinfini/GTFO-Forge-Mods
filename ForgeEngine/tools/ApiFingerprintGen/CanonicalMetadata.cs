using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

internal sealed partial class CanonicalApi
{
    // Roslyn deliberately consumes several metadata encodings rather than exposing attributes for
    // them. Read the original metadata too: validate before normalization, and retain interop tables.
    // Supplemental rows use decoded signatures, never tokens, row order, or overload names alone.
    private void ReadMetadataContracts()
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var decoder = new ContractMetadataTypes(this, reader);
        ReadInteropTableCounts(pe, reader);
        // Index every original row: metadata with an invalid sorted-table promise must not hide
        // duplicate attributes from MetadataReader's parent-range lookup.
        var attributeRows = reader.CustomAttributes.GroupBy(handle => reader.GetCustomAttribute(handle).Parent).ToDictionary(group => group.Key, group => group.ToArray());
        void Validate(EntityHandle parent, bool contextOnly = false)
        {
            var attributes = attributeRows.GetValueOrDefault(parent) ?? Array.Empty<CustomAttributeHandle>();
            if (!reader.GetCustomAttributes(parent).ToHashSet().SetEquals(attributes)) throw new InvalidDataException("Inconsistent public custom attribute table ordering.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var handle in attributes)
            {
                var attribute = reader.GetCustomAttribute(handle);
                var owner = decoder.AttributeOwner(attribute.Constructor);
                if (contextOnly && !TransportAttributes.Contains(owner.Name!)) continue;
                if (ImplementationAttributes.Contains(owner.Name!)) continue;
                if (!seen.Add(Encode(owner.Key)) && !decoder.AllowsMultiple(owner)) throw new InvalidDataException("Duplicate public contract attribute: " + owner.Name);
                try
                {
                    var decoded = attribute.DecodeValue(decoder);
                    if (owner.Name is "System.Runtime.CompilerServices.NullableAttribute" or "System.Runtime.CompilerServices.NullableContextAttribute") ValidateNullable(decoded.FixedArguments);
                    decoder.ValidateAttribute(attribute);
                }
                catch (Exception error) when (error is BadImageFormatException or InvalidOperationException or ArgumentException)
                {
                    throw new InvalidDataException("Malformed public contract attribute: " + owner.Name, error);
                }
            }
        }
        void Generics(GenericParameterHandleCollection parameters)
        {
            foreach (var handle in parameters)
            {
                var parameter = reader.GetGenericParameter(handle);
                Validate(handle);
                foreach (var constraint in parameter.GetConstraints()) Validate(constraint);
            }
        }
        Validate(System.Reflection.Metadata.Ecma335.MetadataTokens.EntityHandle(System.Reflection.Metadata.Ecma335.TableIndex.Assembly, 1), true);
        Validate(System.Reflection.Metadata.Ecma335.MetadataTokens.EntityHandle(System.Reflection.Metadata.Ecma335.TableIndex.Module, 1), true);
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var identity = decoder.GetTypeFromDefinition(reader, typeHandle, 0);
            if (!VisibleMetadataType(reader, typeHandle)) continue;
            var type = reader.GetTypeDefinition(typeHandle);
            Validate(typeHandle);
            Generics(type.GetGenericParameters());
            var explicitBodies = new HashSet<MethodDefinitionHandle>();
            foreach (var implementationHandle in type.GetMethodImplementations())
            {
                var implementation = reader.GetMethodImplementation(implementationHandle);
                if (implementation.MethodBody.Kind != HandleKind.MethodDefinition) continue;
                var declaringType = decoder.MethodOwner(implementation.MethodDeclaration);
                if (Visible(decoder.Resolve(declaringType with { Arguments = default }))) explicitBodies.Add((MethodDefinitionHandle)implementation.MethodBody);
            }
            bool ExposedMetadata(MethodDefinitionHandle handle)
            {
                if (handle.IsNil) return false;
                var access = reader.GetMethodDefinition(handle).Attributes & MethodAttributes.MemberAccessMask;
                return access is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem || explicitBodies.Contains(handle);
            }
            foreach (var methodHandle in type.GetMethods())
            {
                if (!ExposedMetadata(methodHandle)) continue;
                var method = reader.GetMethodDefinition(methodHandle);
                Validate(methodHandle);
                Generics(method.GetGenericParameters());
                var marshals = new SortedDictionary<int, object>();
                var directions = new SortedDictionary<int, int>();
                var positions = new HashSet<int>();
                var signature = method.DecodeSignature(decoder, null);
                foreach (var parameterHandle in method.GetParameters())
                {
                    var parameter = reader.GetParameter(parameterHandle);
                    var position = parameter.SequenceNumber;
                    if (position > signature.ParameterTypes.Length || !positions.Add(position)) throw new InvalidDataException("Invalid public parameter metadata sequence.");
                    Validate(parameterHandle);
                    var direction = parameter.Attributes & (ParameterAttributes.In | ParameterAttributes.Out);
                    var byRef = position > 0 && signature.ParameterTypes[position - 1].ByReference;
                    // C# out and readonly in are already represented by RefKind. Preserve every
                    // additional direction contract, including [In] ref and [In, Out] ref.
                    var redundant = byRef && (direction == ParameterAttributes.Out || direction == ParameterAttributes.In && parameter.GetCustomAttributes().Any(handle => decoder.AttributeOwner(reader.GetCustomAttribute(handle).Constructor).Name == "System.Runtime.CompilerServices.IsReadOnlyAttribute"));
                    if (direction != 0 && !redundant) directions.Add(position, (int)direction);
                    ValidateInteropCount(parameterHandle, (parameter.Attributes & ParameterAttributes.HasFieldMarshal) != 0, marshalRows);
                    var descriptor = parameter.GetMarshallingDescriptor();
                    if (!descriptor.IsNil) marshals.Add(position, decoder.MarshalDescriptor(descriptor));
                }
                ValidateInteropCount(methodHandle, (method.Attributes & MethodAttributes.PinvokeImpl) != 0, importRows);
                var import = method.GetImport();
                var preserveSignature = (method.ImplAttributes & MethodImplAttributes.PreserveSig) != 0;
                if (marshals.Count == 0 && directions.Count == 0 && import.Module.IsNil && (method.Attributes & MethodAttributes.PinvokeImpl) == 0 && !preserveSignature) continue;
                object? pinvoke = null;
                if (!import.Module.IsNil)
                {
                    var flags = (int)import.Attributes;
                    if ((flags & ~0x3777) != 0 || (flags & 0x0700) is < 0x0100 or > 0x0500 || (flags & 0x0030) == 0x0030 || (flags & 0x3000) == 0x3000)
                        throw new InvalidDataException("Invalid public P/Invoke flags.");
                    if (reader.GetString(import.Name).Length == 0 || reader.GetString(reader.GetModuleReference(import.Module).Name).Length == 0)
                        throw new InvalidDataException("Missing public P/Invoke name.");
                    pinvoke = Row(("module", reader.GetString(reader.GetModuleReference(import.Module).Name)), ("entryPoint", reader.GetString(import.Name)),
                        ("attributes", ((int)import.Attributes).ToString(System.Globalization.CultureInfo.InvariantCulture)), ("preserveSignature", (method.ImplAttributes & MethodImplAttributes.PreserveSig) != 0));
                }
                else if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0) throw new InvalidDataException("Missing public P/Invoke import metadata.");
                rows.Add(Row(("kind", "method-interop"), ("owner", identity.Key), ("name", reader.GetString(method.Name)), ("signature", ContractMetadataTypes.Signature(signature)),
                    ("import", pinvoke), ("preserveSignature", preserveSignature), ("parameterDirections", directions.Select(pair => Row(("position", pair.Key), ("flags", pair.Value))).ToArray()),
                    ("marshalling", marshals.Select(pair => Row(("position", pair.Key), ("descriptor", pair.Value))).ToArray())));
            }
            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                var access = field.Attributes & FieldAttributes.FieldAccessMask;
                if (access is not (FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem)) continue;
                Validate(fieldHandle);
                if ((field.Attributes & FieldAttributes.PinvokeImpl) != 0) throw new InvalidDataException("Unsupported public field import metadata.");
                ValidateInteropCount(fieldHandle, false, importRows);
                ValidateInteropCount(fieldHandle, (field.Attributes & FieldAttributes.HasFieldMarshal) != 0, marshalRows);
                var descriptor = field.GetMarshallingDescriptor();
                if (!descriptor.IsNil) rows.Add(Row(("kind", "field-interop"), ("owner", identity.Key), ("name", reader.GetString(field.Name)),
                    ("signature", field.DecodeSignature(decoder, null).Key), ("marshalling", decoder.MarshalDescriptor(descriptor))));
            }
            foreach (var propertyHandle in type.GetProperties())
            {
                var property = reader.GetPropertyDefinition(propertyHandle); var accessors = property.GetAccessors();
                if (ExposedMetadata(accessors.Getter) || ExposedMetadata(accessors.Setter) || accessors.Others.Any(ExposedMetadata)) Validate(propertyHandle);
            }
            foreach (var eventHandle in type.GetEvents())
            {
                var item = reader.GetEventDefinition(eventHandle); var accessors = item.GetAccessors();
                if (ExposedMetadata(accessors.Adder) || ExposedMetadata(accessors.Remover) || ExposedMetadata(accessors.Raiser) || accessors.Others.Any(ExposedMetadata)) Validate(eventHandle);
            }
        }
    }

    private sealed record ContractMetadataType(object Key, string? Name = null, IAssemblySymbol? Owner = null, PrimitiveTypeCode? Primitive = null, ContractMetadataType? Element = null, bool ByReference = false, ImmutableArray<ContractMetadataType> Arguments = default, int ArrayRank = 0);
    private sealed partial class ContractMetadataTypes(CanonicalApi api, MetadataReader reader) : ISignatureTypeProvider<ContractMetadataType, object?>, ICustomAttributeTypeProvider<ContractMetadataType>
    {
        private readonly HashSet<TypeSpecificationHandle> activeSpecifications = new();
        private static ContractMetadataType Node(string kind, params (string Key, object? Value)[] values) => new(Row(new[] { ("kind", (object?)kind) }.Concat(values).ToArray()));
        public static object Signature(MethodSignature<ContractMetadataType> signature) => Row(("header", signature.Header.RawValue), ("genericArity", signature.GenericParameterCount),
            ("requiredParameters", signature.RequiredParameterCount), ("return", signature.ReturnType.Key), ("parameters", signature.ParameterTypes.Select(type => type.Key).ToArray()));
        private static string DefinitionName(MetadataReader metadata, TypeDefinitionHandle handle)
        {
            var type = metadata.GetTypeDefinition(handle); var parent = type.GetDeclaringType(); var name = metadata.GetString(type.Name);
            if (!parent.IsNil) return DefinitionName(metadata, parent) + "+" + name;
            var ns = metadata.GetString(type.Namespace); return ns.Length == 0 ? name : ns + "." + name;
        }
        private ContractMetadataType Named(string name, IAssemblySymbol owner) => new(Row(("kind", "named"), ("identity", Row(("assembly", api.AssemblyIdentity(owner)), ("name", name)))), name, owner);
        public ContractMetadataType GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind) => Named(DefinitionName(metadata, handle), api.MetadataAssembly);
        public ContractMetadataType GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var type = metadata.GetTypeReference(handle); var name = metadata.GetString(type.Name); var scope = type.ResolutionScope;
            if (scope.Kind == HandleKind.TypeReference)
            {
                var parent = GetTypeFromReference(metadata, (TypeReferenceHandle)scope, 0);
                return Named(parent.Name + "+" + name, parent.Owner!);
            }
            var ns = metadata.GetString(type.Namespace); if (ns.Length != 0) name = ns + "." + name;
            if (scope.Kind == HandleKind.ModuleDefinition) return Named(name, api.MetadataAssembly);
            if (scope.Kind != HandleKind.AssemblyReference) throw new InvalidDataException("Unsupported public metadata type scope: " + scope.Kind);
            var reference = metadata.GetAssemblyReference((AssemblyReferenceHandle)scope);
            var token = metadata.GetBlobBytes(reference.PublicKeyOrToken);
            if ((reference.Flags & AssemblyFlags.PublicKey) != 0) token = System.Security.Cryptography.SHA1.HashData(token)[^8..].Reverse().ToArray();
            var assemblyName = metadata.GetString(reference.Name);
            var owner = api.MetadataAssembly.Modules.SelectMany(module => module.ReferencedAssemblySymbols).FirstOrDefault(candidate => candidate.Identity.Name == assemblyName && candidate.Identity.PublicKeyToken.SequenceEqual(token))
                ?? throw new InvalidDataException("Unresolved public metadata assembly: " + assemblyName);
            return Named(name, owner);
        }
        public INamedTypeSymbol Resolve(ContractMetadataType type)
        {
            var definition = type.Owner?.GetTypeByMetadataName(type.Name!) ?? type.Owner?.ResolveForwardedType(type.Name!)
                ?? throw new InvalidDataException("Unresolved public metadata type: " + type.Name);
            if (type.Arguments.IsDefault) return definition;
            var chain = new Stack<INamedTypeSymbol>(); for (var current = definition; current != null; current = current.ContainingType) chain.Push(current);
            INamedTypeSymbol? constructed = null; var offset = 0;
            foreach (var component in chain)
            {
                var part = constructed == null ? component : constructed.GetTypeMembers(component.Name, component.Arity).Single();
                if (component.Arity > 0)
                {
                    if (offset + component.Arity > type.Arguments.Length) throw new BadImageFormatException("Invalid metadata generic arity.");
                    part = part.Construct(type.Arguments.Skip(offset).Take(component.Arity).Select(ResolveType).ToArray()); offset += component.Arity;
                }
                constructed = part;
            }
            if (offset != type.Arguments.Length) throw new BadImageFormatException("Invalid metadata generic arity.");
            return constructed!;
        }
        private ITypeSymbol ResolveType(ContractMetadataType type)
        {
            if (type.Element is { } element) return api.MetadataCompilation.CreateArrayTypeSymbol(ResolveType(element), type.ArrayRank);
            if (type.Primitive is { } primitive) return api.MetadataCompilation.GetSpecialType(PrimitiveSpecialType(primitive));
            return Resolve(type);
        }
        private ContractMetadataType EntityType(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)handle, 0),
            _ => throw new InvalidDataException("Unsupported public metadata type handle: " + handle.Kind)
        };
        public ContractMetadataType MethodOwner(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.MethodDefinition => EntityType(reader.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType()),
            HandleKind.MemberReference => EntityType(reader.GetMemberReference((MemberReferenceHandle)handle).Parent),
            _ => throw new InvalidDataException("Unsupported contract method handle: " + handle.Kind)
        };
        public ContractMetadataType AttributeOwner(EntityHandle handle) => MethodOwner(handle);
        public ContractMetadataType GetTypeFromSpecification(MetadataReader metadata, object? context, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            if (activeSpecifications.Count >= 64 || !activeSpecifications.Add(handle)) throw new InvalidDataException("Cyclic or excessively nested metadata signature.");
            try { return metadata.GetTypeSpecification(handle).DecodeSignature(this, context); } finally { activeSpecifications.Remove(handle); }
        }
        public ContractMetadataType GetArrayType(ContractMetadataType element, ArrayShape shape) => new(Row(("kind", "array"), ("element", element.Key), ("rank", shape.Rank), ("sizes", shape.Sizes.ToArray()), ("lowerBounds", shape.LowerBounds.ToArray())), Element: element, ArrayRank: shape.Rank);
        public ContractMetadataType GetSZArrayType(ContractMetadataType element) => new(Row(("kind", "szArray"), ("element", element.Key)), Element: element, ArrayRank: 1);
        public ContractMetadataType GetByReferenceType(ContractMetadataType element) => new(Row(("kind", "byRef"), ("element", element.Key)), ByReference: true);
        public ContractMetadataType GetPointerType(ContractMetadataType element) => Node("pointer", ("element", element.Key));
        public ContractMetadataType GetPinnedType(ContractMetadataType element) => throw new InvalidDataException("Unexpected pinned public signature.");
        public ContractMetadataType GetFunctionPointerType(MethodSignature<ContractMetadataType> signature) => Node("functionPointer", ("signature", Signature(signature)));
        public ContractMetadataType GetGenericInstantiation(ContractMetadataType genericType, ImmutableArray<ContractMetadataType> arguments) => new(Row(("kind", "generic"), ("type", genericType.Key), ("arguments", arguments.Select(type => type.Key).ToArray())), genericType.Name, genericType.Owner, Arguments: arguments);
        public ContractMetadataType GetGenericMethodParameter(object? context, int index) => Node("methodParameter", ("ordinal", index));
        public ContractMetadataType GetGenericTypeParameter(object? context, int index) => context is ImmutableArray<ContractMetadataType> arguments && !arguments.IsDefault && index < arguments.Length ? arguments[index] : Node("typeParameter", ("ordinal", index));
        public ContractMetadataType GetModifiedType(ContractMetadataType modifier, ContractMetadataType unmodified, bool isRequired) => new(Row(("kind", "modified"), ("required", isRequired), ("modifier", modifier.Key), ("type", unmodified.Key)), ByReference: unmodified.ByReference);
        public ContractMetadataType GetPrimitiveType(PrimitiveTypeCode code) => new(Row(("kind", "primitive"), ("type", code.ToString())), Primitive: code);
        public ContractMetadataType GetSystemType() => new("System.Type", "System.Type");
        public bool IsSystemType(ContractMetadataType type) => type.Name == "System.Type";
        public ContractMetadataType GetTypeFromSerializedName(string name) => SerializedType(name, 0);
        private ContractMetadataType SerializedType(string name, int nesting)
        {
            if (nesting >= 64) throw new InvalidDataException("Serialized contract type nesting exceeds 64.");
            var depth = 0; var comma = -1;
            for (var index = 0; index < name.Length; index++)
            {
                if (name[index] == '[') depth++;
                else if (name[index] == ']') { if (--depth < 0) throw new BadImageFormatException("Unbalanced serialized type name."); }
                else if (name[index] == ',' && depth == 0) { comma = index; break; }
            }
            if (depth != 0) throw new BadImageFormatException("Unbalanced serialized type name.");
            var typeText = (comma < 0 ? name : name[..comma]).Trim();
            var suffix = typeText.IndexOfAny(new[] { '[', '*', '&' }); var typeName = suffix < 0 ? typeText : typeText[..suffix];
            if (typeName.Contains('\\')) throw new InvalidDataException("Escaped serialized metadata type names require an explicit decoder.");
            IEnumerable<IAssemblySymbol> candidates = new[] { api.MetadataAssembly }.Concat(api.ReferenceAssemblies);
            if (comma >= 0)
            {
                var requested = new AssemblyName(name[(comma + 1)..].Trim());
                candidates = candidates.Where(candidate => candidate.Identity.Name == requested.Name && (requested.GetPublicKeyToken() is not { } token || candidate.Identity.PublicKeyToken.SequenceEqual(token)));
            }
            var matches = candidates.Select(candidate => (Owner: candidate, Type: candidate.GetTypeByMetadataName(typeName) ?? candidate.ResolveForwardedType(typeName))).Where(match => match.Type != null).ToArray();
            if (matches.Length == 0) throw new InvalidDataException("Unresolved serialized contract attribute type: " + name);
            var first = matches[0];
            if (matches.Any(match => !SymbolEqualityComparer.Default.Equals(match.Type, first.Type))) throw new InvalidDataException("Ambiguous serialized contract attribute type: " + name);
            // Use the resolved defining assembly, so equivalent forwarded type spellings normalize too.
            var result = Named(typeName, first.Type!.ContainingAssembly);
            var position = suffix < 0 ? typeText.Length : suffix;
            while (position < typeText.Length)
            {
                var code = typeText[position];
                if (code == '*') { result = GetPointerType(result); position++; continue; }
                if (code == '&') { result = GetByReferenceType(result); position++; continue; }
                if (code != '[') throw new BadImageFormatException("Invalid serialized type suffix.");
                var end = position + 1; depth = 1;
                for (; end < typeText.Length && depth > 0; end++) { if (typeText[end] == '[') depth++; else if (typeText[end] == ']') depth--; }
                if (depth != 0) throw new BadImageFormatException("Unbalanced serialized type suffix.");
                var contents = typeText[(position + 1)..(end - 1)];
                if (contents.Length == 0) result = GetSZArrayType(result);
                else if (contents.All(character => character == ',')) result = GetArrayType(result, new ArrayShape(contents.Length + 1, ImmutableArray<int>.Empty, ImmutableArray<int>.Empty));
                else if (contents == "*") result = GetArrayType(result, new ArrayShape(1, ImmutableArray<int>.Empty, ImmutableArray<int>.Empty));
                else
                {
                    var arguments = ImmutableArray.CreateBuilder<ContractMetadataType>(); var begin = 0; depth = 0;
                    for (var index = 0; index <= contents.Length; index++)
                    {
                        if (index < contents.Length) { if (contents[index] == '[') depth++; else if (contents[index] == ']') depth--; }
                        if (index != contents.Length && (contents[index] != ',' || depth != 0)) continue;
                        var argument = contents[begin..index].Trim();
                        if (argument.StartsWith('[') && argument.EndsWith(']')) argument = argument[1..^1];
                        arguments.Add(SerializedType(argument, nesting + 1)); begin = index + 1;
                    }
                    var arity = 0; for (var current = first.Type; current != null; current = current.ContainingType) arity += current.Arity;
                    if (arguments.Count != arity || arity == 0) throw new BadImageFormatException("Invalid serialized generic arity.");
                    result = GetGenericInstantiation(result, arguments.ToImmutable());
                }
                position = end;
            }
            return result;
        }
        public bool AllowsMultiple(ContractMetadataType type)
        {
            for (var symbol = Resolve(type); symbol != null; symbol = symbol.BaseType)
            {
                var usage = symbol.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == "System.AttributeUsageAttribute");
                if (usage != null) return usage.NamedArguments.Any(pair => pair.Key == "AllowMultiple" && pair.Value.Value is true);
            }
            return false;
        }
        public PrimitiveTypeCode GetUnderlyingEnumType(ContractMetadataType type) => Resolve(type).EnumUnderlyingType?.SpecialType switch
        {
            SpecialType.System_SByte => PrimitiveTypeCode.SByte,
            SpecialType.System_Byte => PrimitiveTypeCode.Byte,
            SpecialType.System_Int16 => PrimitiveTypeCode.Int16,
            SpecialType.System_UInt16 => PrimitiveTypeCode.UInt16,
            SpecialType.System_Int32 => PrimitiveTypeCode.Int32,
            SpecialType.System_UInt32 => PrimitiveTypeCode.UInt32,
            SpecialType.System_Int64 => PrimitiveTypeCode.Int64,
            SpecialType.System_UInt64 => PrimitiveTypeCode.UInt64,
            _ => throw new InvalidDataException("Invalid contract attribute enum: " + type.Name)
        };
        public void ValidateAttribute(CustomAttribute attribute)
        {
            var owner = AttributeOwner(attribute.Constructor);
            var signature = attribute.Constructor.Kind switch
            {
                HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).DecodeSignature(this, owner.Arguments),
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).DecodeMethodSignature(this, owner.Arguments),
                _ => throw new InvalidDataException("Invalid public contract attribute constructor.")
            };
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) throw new BadImageFormatException("Invalid custom attribute prolog.");
            foreach (var type in signature.ParameterTypes) ReadValue(ref blob, type, 0);
            var namedCount = blob.ReadUInt16(); var names = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < namedCount; index++)
            {
                var kind = blob.ReadByte();
                if (kind is not (0x53 or 0x54)) throw new BadImageFormatException("Invalid custom attribute named argument kind.");
                var type = ReadNamedType(ref blob, 0); var name = blob.ReadSerializedString();
                if (string.IsNullOrEmpty(name) || !names.Add(name)) throw new BadImageFormatException("Invalid custom attribute named argument name.");
                ValidateNamedMember(AttributeOwner(attribute.Constructor), kind, name, type);
                ReadValue(ref blob, type, 0);
            }
            if (blob.RemainingBytes != 0) throw new BadImageFormatException("Trailing custom attribute data.");
        }
        private void ValidateNamedMember(ContractMetadataType owner, byte kind, string name, ContractMetadataType encodedType)
        {
            for (var symbol = Resolve(owner); symbol != null; symbol = symbol.BaseType)
            {
                foreach (var member in symbol.GetMembers(name))
                {
                    ITypeSymbol? type = member switch
                    {
                        IFieldSymbol field when kind == 0x53 && !field.IsStatic && !field.IsReadOnly && !field.IsConst && field.DeclaredAccessibility == Accessibility.Public => field.Type,
                        IPropertySymbol property when kind == 0x54 && !property.IsStatic && property.Parameters.Length == 0 && property.SetMethod?.DeclaredAccessibility == Accessibility.Public => property.Type,
                        _ => null
                    };
                    if (type != null && AttributeTypeMatches(encodedType, type)) return;
                }
            }
            throw new BadImageFormatException("Unknown or mismatched custom attribute named member: " + name);
        }
        private bool AttributeTypeMatches(ContractMetadataType encoded, ITypeSymbol type)
        {
            if (encoded.Element is { } element) return type is IArrayTypeSymbol { IsSZArray: true } array && AttributeTypeMatches(element, array.ElementType);
            if (IsSystemType(encoded)) return type.ToDisplayString() == "System.Type";
            if (encoded.Primitive is { } primitive) return type.SpecialType == PrimitiveSpecialType(primitive);
            return type.TypeKind == TypeKind.Enum && SymbolEqualityComparer.Default.Equals(Resolve(encoded), type);
        }
        private static SpecialType PrimitiveSpecialType(PrimitiveTypeCode code) => code switch
        {
            PrimitiveTypeCode.Boolean => SpecialType.System_Boolean,
            PrimitiveTypeCode.Char => SpecialType.System_Char,
            PrimitiveTypeCode.SByte => SpecialType.System_SByte,
            PrimitiveTypeCode.Byte => SpecialType.System_Byte,
            PrimitiveTypeCode.Int16 => SpecialType.System_Int16,
            PrimitiveTypeCode.UInt16 => SpecialType.System_UInt16,
            PrimitiveTypeCode.Int32 => SpecialType.System_Int32,
            PrimitiveTypeCode.UInt32 => SpecialType.System_UInt32,
            PrimitiveTypeCode.Int64 => SpecialType.System_Int64,
            PrimitiveTypeCode.UInt64 => SpecialType.System_UInt64,
            PrimitiveTypeCode.Single => SpecialType.System_Single,
            PrimitiveTypeCode.Double => SpecialType.System_Double,
            PrimitiveTypeCode.String => SpecialType.System_String,
            PrimitiveTypeCode.Object => SpecialType.System_Object,
            _ => SpecialType.None
        };
        private ContractMetadataType ReadNamedType(ref BlobReader blob, int depth)
        {
            if (depth >= 64) throw new BadImageFormatException("Excessive custom attribute type nesting.");
            var code = blob.ReadByte();
            return code switch
            {
                0x1d => GetSZArrayType(ReadNamedType(ref blob, depth + 1)),
                0x50 => GetSystemType(),
                0x51 => GetPrimitiveType(PrimitiveTypeCode.Object),
                0x55 => GetTypeFromSerializedName(blob.ReadSerializedString() ?? throw new BadImageFormatException("Missing enum type.")),
                >= 0x02 and <= 0x0e => GetPrimitiveType((PrimitiveTypeCode)code),
                _ => throw new BadImageFormatException("Invalid custom attribute argument type.")
            };
        }
        private void ReadValue(ref BlobReader blob, ContractMetadataType type, int depth)
        {
            if (depth >= 64) throw new BadImageFormatException("Excessive custom attribute nesting.");
            if (type.Element is { } element)
            {
                var count = blob.ReadInt32(); if (count == -1) return;
                if (count < 0 || count > blob.RemainingBytes) throw new BadImageFormatException("Invalid custom attribute array size.");
                for (var index = 0; index < count; index++) ReadValue(ref blob, element, depth + 1);
                return;
            }
            if (IsSystemType(type)) { _ = blob.ReadSerializedString(); return; }
            var primitive = type.Primitive ?? GetUnderlyingEnumType(type);
            switch (primitive)
            {
                case PrimitiveTypeCode.Object: ReadValue(ref blob, ReadNamedType(ref blob, depth + 1), depth + 1); break;
                case PrimitiveTypeCode.String: _ = blob.ReadSerializedString(); break;
                case PrimitiveTypeCode.Boolean: if (blob.ReadByte() > 1) throw new BadImageFormatException("Invalid attribute boolean."); break;
                case PrimitiveTypeCode.SByte: case PrimitiveTypeCode.Byte: _ = blob.ReadByte(); break;
                case PrimitiveTypeCode.Char: case PrimitiveTypeCode.Int16: case PrimitiveTypeCode.UInt16: _ = blob.ReadUInt16(); break;
                case PrimitiveTypeCode.Int32: case PrimitiveTypeCode.UInt32: case PrimitiveTypeCode.Single: _ = blob.ReadUInt32(); break;
                case PrimitiveTypeCode.Int64: case PrimitiveTypeCode.UInt64: case PrimitiveTypeCode.Double: _ = blob.ReadUInt64(); break;
                default: throw new BadImageFormatException("Unsupported custom attribute argument type.");
            }
        }
    }
}
