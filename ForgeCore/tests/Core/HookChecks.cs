using System.Reflection;
using System.Reflection.Emit;

namespace ForgeCore.Tests;

/// <summary>One game method, one package: a second owner is refused and the startup check finds a method two Forge packages patch
/// (detection).</summary>
internal static class HookChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Owners(check);
        SharedBodies(check);
    }

    /// <summary>Detection: a body two method infos point at is found, and a patch target on it is refused unless the registry names
    /// that exact body and set; the registry ForgeCore ships parses.</summary>
    private static void SharedBodies(Action<bool, string> check)
    {
        IntPtr body = new(0x1000), own = new(0x2000), fire = new(0x10), reload = new(0x20), alone = new(0x30);
        var shared = SharedNativeBodies.Shared(new[] { (body, fire), (own, alone), (body, reload), (body, fire), (IntPtr.Zero, new IntPtr(0x40)) });
        check(shared.Count == 1 && shared[body].SequenceEqual(new[] { fire, reload }), "a body two methods share is found once, with both methods; an unshared body is not");
        var entries = new[] { new SharedNativeBodies.Entry("Gun.Fire", 0x1000, new[] { "Gun.Reload" }) };
        check(SharedNativeBodies.Decide("Gun.Fire", 0x1000, new[] { "Gun.Fire", "Gun.Reload" }, Array.Empty<SharedNativeBodies.Entry>()) == SharedNativeBodies.Reason,
            "a patch target on a shared body nobody registered is refused");
        check(SharedNativeBodies.Decide("Gun.Fire", 0x1000, new[] { "Gun.Reload", "Gun.Fire" }, entries) == null, "the exact registered body and set is admitted");
        check(SharedNativeBodies.Decide("Gun.Fire", 0x1000, new[] { "Gun.Fire", "Gun.Reload", "Gun.Drop" }, entries) == SharedNativeBodies.Reason
            && SharedNativeBodies.Decide("Gun.Fire", 0x2000, new[] { "Gun.Fire", "Gun.Reload" }, entries) == SharedNativeBodies.Reason
            && SharedNativeBodies.Decide("Gun.Reload", 0x1000, new[] { "Gun.Fire", "Gun.Reload" }, entries) == SharedNativeBodies.Reason,
            "a registered body that gained a sharer, moved, or is patched through another method is refused");
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "shared-native-bodies.json"));
        check(SharedNativeBodies.Parse(file).Length > 0, "ForgeCore's shared-native-bodies.json parses");
    }

    private static void Target() { }
    private static void Other() { }

    // A static method compiled into an assembly of the given name, as a package's patch callback would be.
    private static MethodInfo Callback(string assembly)
    {
        var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assembly), AssemblyBuilderAccess.Run).DefineDynamicModule(assembly);
        var type = module.DefineType("Callbacks", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        type.DefineMethod("Postfix", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes).GetILGenerator().Emit(OpCodes.Ret);
        return type.CreateType()!.GetMethod("Postfix")!;
    }

    private static void Owners(Action<bool, string> check)
    {
        var target = typeof(HookChecks).GetMethod(nameof(Target), BindingFlags.NonPublic | BindingFlags.Static)!;
        var other = typeof(HookChecks).GetMethod(nameof(Other), BindingFlags.NonPublic | BindingFlags.Static)!;
        check(HookOwners.Claim("ForgeMap", target, "set") == null && HookOwners.Claim("ForgeMap", target, "set") == ""
            && HookOwners.Claim("ForgeMap", target, "changed") == "native-hook-declaration-changed", "an owner claims once, again is a no-op, a changed set refuses");
        check(HookOwners.Claim("ForgeEnemy", target, "set") == "native-hook-writer-conflict existing=ForgeMap"
            && HookOwners.Refusal("ForgeEnemy", target) == "native-hook-writer-conflict existing=ForgeMap", "a second owner is refused");
        HookOwners.Release("ForgeEnemy", target);
        check(HookOwners.OwnerOf(target) == "ForgeMap", "another package cannot release the owner's claim");
        HookOwners.Claim("ForgeMap", other, "set");

        MethodInfo map = Callback("ForgeMap.Native"), enemy = Callback("ForgeEnemy.Native"), foreign = Callback("SomeoneElse");
        var records = new Dictionary<MethodBase, (string, MethodInfo)[]>
        {
            [target] = new[] { (HookOwners.OwnPrefix + "forge.module.gtfo.map", map), ("NAinfini.ForgeEngine.Enemy", enemy) },
            [other] = new[] { (HookOwners.OwnPrefix + "forge.module.gtfo.map", map), (HookOwners.ObservePrefix + "ForgeEnemy", enemy), ("someone", foreign) },
        };
        var shared = HookOwners.Shared(method => records[method]);
        check(shared.Count == 1 && shared[0] == typeof(HookChecks).FullName + "." + nameof(Target) + ": ForgeEnemy, ForgeMap",
            "the startup check names the one method two Forge packages patch; an observer or another mod is not a second package");
        HookOwners.Release("ForgeMap", target); HookOwners.Release("ForgeMap", other);
    }

}
