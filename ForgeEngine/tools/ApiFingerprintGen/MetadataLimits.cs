using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

/// <summary>Reject metadata that this decoder cannot promise to preserve. It must not silently erase a newer
/// anti-constraint into an older ordinary generic parameter.</summary>
internal static class MetadataLimits
{
    internal static void Validate(string assemblyPath, string referenceDirectory)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var available = Directory.EnumerateFiles(referenceDirectory, "*.dll").Select(AssemblyName.GetAssemblyName).ToLookup(name => name.Name!, StringComparer.OrdinalIgnoreCase);
        foreach (var handle in reader.AssemblyReferences)
        {
            var reference = reader.GetAssemblyReference(handle);
            var name = reader.GetString(reference.Name);
            var token = reader.GetBlobBytes(reference.PublicKeyOrToken);
            if ((reference.Flags & AssemblyFlags.PublicKey) != 0) token = System.Security.Cryptography.SHA1.HashData(token)[^8..].AsEnumerable().Reverse().ToArray();
            if (!available[name].Any(candidate => (candidate.GetPublicKeyToken() ?? Array.Empty<byte>()).SequenceEqual(token)))
                throw new InvalidDataException("Missing or wrong-identity SDK metadata reference: " + name);
        }
        const GenericParameterAttributes known = GenericParameterAttributes.VarianceMask | GenericParameterAttributes.SpecialConstraintMask;
        bool Visible(TypeDefinitionHandle handle)
        {
            var value = reader.GetTypeDefinition(handle);
            var visibility = value.Attributes & TypeAttributes.VisibilityMask;
            var parent = value.GetDeclaringType();
            return parent.IsNil ? visibility == TypeAttributes.Public
                : Visible(parent) && visibility is TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem;
        }
        foreach (var handle in reader.TypeDefinitions)
        {
            if (!Visible(handle)) continue;
            var type = reader.GetTypeDefinition(handle);
            void Parameters(GenericParameterHandleCollection parameters)
            {
                foreach (var parameter in parameters)
                    if ((reader.GetGenericParameter(parameter).Attributes & ~known) != 0)
                        throw new InvalidDataException("Unsupported generic anti-constraint flags; this canonical format requires an explicit decoder update: " + reader.GetString(type.Name));
            }
            Parameters(type.GetGenericParameters());
            var explicitBodies = type.GetMethodImplementations().Select(row => reader.GetMethodImplementation(row).MethodBody).ToHashSet();
            foreach (var method in type.GetMethods())
            {
                var value = reader.GetMethodDefinition(method);
                var visibility = value.Attributes & MethodAttributes.MemberAccessMask;
                if (visibility is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem || explicitBodies.Contains(method)) Parameters(value.GetGenericParameters());
            }
        }
    }
}
