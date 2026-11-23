using Mono.Cecil;
using CilCode = Mono.Cecil.Cil.Code;

namespace ForgePlayer.Tests.PlayerNativeEvidence;

[Trait("Category", "Native")]
public sealed class EqPlayerNativeEvidenceTests
{
    private static string Input(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        Assert.False(string.IsNullOrWhiteSpace(value), "Required read-only input: " + name);
        return value!;
    }
    private static TypeDefinition Type(AssemblyDefinition assembly, string name)
        => assembly.MainModule.Types.Single(type => type.Name == name);
    private static MethodReference[] Calls(MethodDefinition method) => method.Body.Instructions
        .Where(instruction => instruction.Operand is MethodReference).Select(instruction => (MethodReference)instruction.Operand).ToArray();

    [Fact]
    public void shipped_healing_calls_the_incremental_native_entry_with_its_source_and_has_no_absolute_fallback()
    {
        using var native = AssemblyDefinition.ReadAssembly(Input("FORGE_EQ_PLAYER_NATIVE"));
        var receiver = Type(native, "PlayerHealthReceiver");
        var submit = receiver.Methods.Single(method => method.Name == "TrySubmit");
        var call = Assert.Single(Calls(submit), method => method.Name == "AddHealth");
        Assert.Equal("Dam_SyncedDamageBase", call.DeclaringType.Name);
        Assert.Equal(new[] { "System.Single", "Agents.Agent" }, call.Parameters.Select(parameter => parameter.ParameterType.FullName));
        int index = submit.Body.Instructions.ToList().FindIndex(instruction => instruction.Operand == call);
        Assert.Contains(submit.Body.Instructions.Skip(Math.Max(0, index - 3)).Take(3), instruction => instruction.OpCode.Code == CilCode.Ldarg_3);
        foreach (var type in new[] { receiver, Type(native, "PlayerHealthAction") })
            Assert.DoesNotContain(type.Methods.Where(method => method.HasBody).SelectMany(Calls), method => method.Name == "SendSetHealth");
    }
}
