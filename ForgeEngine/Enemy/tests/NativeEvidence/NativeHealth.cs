using System.Reflection.PortableExecutable;
using Iced.Intel;

internal static class NativeHealth
{
    // Offline evidence offsets, never production function pointers or invoked game methods.
    private const string SupportedHash = "C6A5C3CD8CA5FE2A8C1A71A3D107663E8CBF01404820DBBDA2EA10C4BFD7BF55";

    // The only signatures whose native bodies this auditor decodes as static-native. The RVA-to-method binding
    // was read from an Il2CppDumper map of this build; the auditor proves instruction shapes at the address,
    // not that binding. ProcessReceivedDamage belongs here because the damage window below decodes its body at
    // that address; without the entry the frozen damage file could declare any RVA for it and still pass.
    internal static readonly IReadOnlyDictionary<string, int> ReviewedRvas = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["System.Void Dam_SyncedDamageBase::SendSetHealth(System.Single)"] = 0x161F790,
        ["System.Void Dam_EnemyDamageBase::ReceiveSetHealth(pSetHealthData)"] = 0x1380D50,
        ["System.Boolean Dam_EnemyDamageBase::ProcessReceivedDamage(System.Single,Agents.Agent,UnityEngine.Vector3,UnityEngine.Vector3,ES_HitreactType,System.Boolean,System.Int32,System.Single,DamageNoiseLevel,System.UInt32)"] = 0x137E570,
    };

    // The damage window this auditor accepts. Each entry names the RVA the decode starts at and the
    // instruction text that must appear, in order, inside that window. Ordering, not adjacency, because the
    // compiler may interleave argument setup; every window starts at an instruction boundary.
    internal sealed record DamageWindowCheck(string Id, int WindowRva, int WindowBytes, string[] Instructions, string Detail);

    internal static readonly IReadOnlyList<DamageWindowCheck> DamageWindow = new[]
    {
        new DamageWindowCheck("damage.window.body", 0x137E570, 0x500,
            new[]
            {
                "mov [rsp+18h],rbx",
                "push rbp",
                "lea rbp,[rsp-30h]",
                "sub rsp,130h",
                "mov r13,r9",
                "mov r12,r8",
                "mov rbx,rcx",
                "pop rbp",
                "ret",
            },
            "ProcessReceivedDamage RVA 0x137E570 decodes as the frozen body: the integer arguments land in r8 and r9, the body ends in a plain return, and no exception path intervenes."),
        new DamageWindowCheck("damage.window.register-damage", 0x137E570, 0x500,
            new[]
            {
                "mov rbx,rcx",
                "call 000000018161F450h",
                "mov rcx,[rbx+0E8h]",
            },
            "The body calls Dam_SyncedDamageBase.RegisterDamage at 0x137E5F7 (RVA 0x161F450) with the scaled damage, then reads the Owner property at +0xE8."),
        new DamageWindowCheck("damage.window.attacker-argument", 0x137E570, 0x500,
            new[]
            {
                "mov rdx,r12",
                "mov rax,[r8+240h]",
                "mov r8,[r8+248h]",
                "call rax",
                "test sil,sil",
            },
            "The attacker argument (moved into r12 by the prologue) is passed to a virtual owner call at 0x137E623, and the Boolean RegisterDamage returned in sil is tested right after; the vtable slot itself is not resolved."),
        new DamageWindowCheck("damage.window.bullet-hit", 0x137EF63, 0x20,
            new[]
            {
                "call 000000018137E570h",
                "movaps xmm9,[rsp+0E0h]",
            },
            "ReceiveBulletDamage calls ProcessReceivedDamage at 0x137EF63 and restores its caller-saved registers; the packet it read supplied the damage, attacker and limb it forwards."),
        new DamageWindowCheck("damage.window.melee-return-value", 0x1380768, 0x20,
            new[]
            {
                "call 000000018137E570h",
                "test al,al",
                "je short 00000001813807A9h",
            },
            "ReceiveMeleeDamage branches on the Boolean returned by ProcessReceivedDamage at 0x1380768, so the value distinguishes an applied hit from a rejected one; the branch target is not a verdict name."),
        new DamageWindowCheck("damage.window.explosion-hit", 0x137F6D7, 0x20,
            new[]
            {
                "call 000000018137E570h",
                "movaps xmm10,[rsp+0E0h]",
            },
            "ReceiveExplosionDamage calls ProcessReceivedDamage at 0x137F6D7; the explosion packet has no attacker field, so the source argument cannot name a player or a deployer."),
        new DamageWindowCheck("damage.window.fire-hit", 0x137FC8C, 0x30,
            new[]
            {
                "call 000000018137E570h",
                "add rsp,0A0h",
                "ret",
            },
            "ReceiveFireDamage and ReceiveFreezeDamage call ProcessReceivedDamage at 0x137FC8C and 0x137FE2C and return immediately, so a damage-over-time tick applies through the same window as a hit."),
        new DamageWindowCheck("damage.window.bullet-local-apply", 0x137D4BD, 0x30,
            new[]
            {
                "call 000000018161F4E0h",
                "test al,al",
                "je short 000000018137D4F6h",
            },
            "BulletDamage calls Dam_SyncedDamageBase.SendLocally at 0x137D4BD (RVA 0x161F4E0) and branches on its result: the entry point applies or replicates the hit itself instead of calling ProcessReceivedDamage directly."),
        // The two senders and the setup that arms them. Each body returns true before its network-flag load when
        // m_onlyToMaster is zero, which is what every enemy damage receiver has: the setup below writes that field
        // as (DamageBaseOwner == PlayerBot) and the enemy override answers Enemy. This is why the host-only
        // submission of a Forge damage row is the provider's own check and not the game's.
        new DamageWindowCheck("damage.window.local-gate", 0x161F4E0, 0x100,
            new[]
            {
                "cmp byte ptr [rbx+25h],0",
                "jne short 000000018161F512h",
                "mov al,1",
                "ret",
                "mov rax,[rax+0B8h]",
                "movzx eax,byte ptr [rax+0B9h]",
            },
            "SendLocally RVA 0x161F4E0 returns true without touching the network flag while m_onlyToMaster (+0x25) is zero, and only reads [net+0xB8]+0xB9 on the branch that field selects."),
        new DamageWindowCheck("damage.window.send-gate", 0x161F5A0, 0x100,
            new[]
            {
                "cmp byte ptr [rbx+25h],0",
                "jne short 000000018161F5D2h",
                "mov al,1",
                "ret",
                "mov rax,[rax+0B8h]",
                "cmp byte ptr [rax+0B9h],0",
                "sete al",
            },
            "SendPacket RVA 0x161F5A0 has the same gate and answers the opposite of SendLocally on the flag branch, so a receiver with m_onlyToMaster zero sends on every machine."),
        new DamageWindowCheck("damage.window.setup-gate", 0x16201B0, 0x100,
            new[]
            {
                "mov rax,[rdx+360h]",
                "mov rdx,[rdx+368h]",
                "call rax",
                "cmp eax,1",
                "mov byte ptr [rbx+24h],1",
                "sete al",
                "mov [rbx+25h],al",
            },
            "Dam_SyncedDamageBase.Setup RVA 0x16201B0 writes m_onlyToMaster as the owner-type getter's answer equal to 1 (PlayerBot); the setters and the health copy sit around it."),
        new DamageWindowCheck("damage.window.enemy-owner", 0x503EF0, 0x40,
            new[]
            {
                "mov eax,2",
                "ret",
            },
            "Dam_EnemyDamageBase.get_DamageBaseOwner RVA 0x503EF0 answers Enemy (2), so Setup leaves m_onlyToMaster at zero for every receiver this provider owns."),
        new DamageWindowCheck("damage.window.receive-slot", 0x137D4C6, 0x30,
            new[]
            {
                "mov r8,[rbx]",
                "mov rax,[r8+400h]",
                "mov r8,[r8+408h]",
                "call rax",
            },
            "BulletDamage's local path calls the receiver through the method-pointer pair at class + 0x400; with the class's inline vtable base at +0x130 that is slot 45, the declared slot of ReceiveBulletDamage, which is the calibration the attacker call's +0x240 pair reads as slot 17."),
        new DamageWindowCheck("damage.window.inflictor-null-guard", 0x1562BD0, 0x120,
            new[]
            {
                "mov rbx,rdx",
                "xor edx,edx",
                "mov rcx,rbx",
                "call 0000000180F0C040h",
                "test al,al",
                "je short 0000000181562C9Fh",
            },
            "EnemyAgent.RegisterDamageInflictor (vtable slot 17, RVA 0x1562BD0) returns immediately when op_Inequality(inflictor, null) is false, so the attacker ProcessReceivedDamage hands over may be null."),
    };

    // The vanilla paths a model attachment's hit part rides (Native/Content/EnemyModelAssemblies.cs). The part is a
    // Dam_EnemyDamageLimb_Custom beside one BoxCollider on the placement root; nothing of it is a Forge hook. Method
    // RVAs were read from the Il2CppDumper map of this build; these windows prove the instruction shapes at them.
    internal static readonly IReadOnlyList<DamageWindowCheck> ModelLimbWindow = new[]
    {
        new DamageWindowCheck("model-limb.bullet-hit-object", 0x145EE9E, 0x490,
            new[]
            {
                "call 000000018243C250h",
                "mov rcx,rax",
                "call 0000000181DD8EC0h",
                "mov rsi,rax",
                "mov edi,ebx",
                "mov rcx,rsi",
                "call 0000000181960980h",
                "mov rdi,[rdi+28h]",
                "test rdi,rdi",
                "jne short 000000018145F32Ch",
                "mov rcx,rsi",
                "call 0000000181960980h",
                "mov rdi,rax",
            },
            "BulletWeapon.BulletHit (RVA 0x145EE10) takes RaycastHit.collider (0x145EE9E), its gameObject into rsi (0x145EEC0), reads GetComponent<ColliderMaterial>(rsi) and, when a material exists, its Damageable (+0x28); with no material or no Damageable it asks GetComponent<IDamageable>() of the same rsi object (0x145F321). There is no GetComponentInParent on this path, so the hit part's collider and limb must sit on one GameObject."),
        new DamageWindowCheck("model-limb.packet-limb-health", 0x137EE28, 0x58,
            new[]
            {
                "cmp byte ptr [rbp-60h],0",
                "jbe near ptr 000000018137EED1h",
                "mov rdx,[rdi+0F8h]",
                "movzx eax,byte ptr [rbp-60h]",
                "cmp eax,[rdx+18h]",
                "jae near ptr 000000018137F022h",
                "mov rdx,[rdx+rax*8+20h]",
                "movss xmm0,dword ptr [rdx+44h]",
                "subss xmm0,xmm6",
                "movss dword ptr [rdx+44h],xmm0",
            },
            "ReceiveBulletDamage (RVA 0x137EC20) indexes DamageLimbs (+0xF8) with the packet's limb byte only when it is above zero, refuses an index past the array length, and writes that limb's m_health (+0x44) on the machine applying the hit (0x137EE79) before it calls ProcessReceivedDamage. No packet carries limb health: every peer's limb indices must agree, and a client holds only the hits it applied itself."),
        new DamageWindowCheck("model-limb.limb-bullet-id", 0x1383A7E, 0x50,
            new[]
            {
                "mov eax,[rsi+20h]",
                "mov [rsp+38h],eax",
                "call 000000018137D0B0h",
            },
            "Dam_EnemyDamageLimb.BulletDamage (RVA 0x1383290) forwards its own m_limbID (+0x20) to Dam_EnemyDamageBase.BulletDamage at 0x1383AC2, so the id written into an added limb is the one the packet carries."),
        new DamageWindowCheck("model-limb.custom-destroy", 0x1616090, 0x2E0,
            new[]
            {
                "cmp byte ptr [rbx+35h],0",
                "mov byte ptr [rbx+35h],1",
                "call 0000000181384D10h",
                "mov rdi,[rbx+78h]",
                "call 0000000180F0C040h",
                "mov rcx,[rbx+78h]",
                "mov rax,[r8+170h]",
                "mov r8,[r8+178h]",
                "call rax",
                "call 000000018140F0F0h",
                "call 0000000181DD8EC0h",
                "call 0000000181DDE380h",
            },
            "Dam_EnemyDamageLimb_Custom.DestroyLimb (RVA 0x1616090) returns once IsDestroyed (+0x35) is set, sets it, calls NotifyLimbDestroyed, fires m_effectOnDestroy (+0x78) through its virtual pair at class + 0x170 (slot 4) when it is not null, applies the screen liquid, and deactivates the limb's own gameObject (0x1616333)."),
        new DamageWindowCheck("model-limb.trigger-effect", 0x1730AC0, 0x130,
            new[]
            {
                "mov rbx,[rdi+18h]",
                "call 0000000180F0C040h",
                "mov rcx,[rdi+18h]",
                "call 0000000181945D40h",
                "call 0000000181948BA0h",
                "mov [rdi+30h],rax",
                "cmp byte ptr [rdi+28h],0",
            },
            "CD_TriggerEffect.Trigger(Vector3, Quaternion) (RVA 0x1730AC0) checks m_linkedEffect (+0x18) against null, takes FX_Manager.GetEffectPool(m_linkedEffect) and AquireEffect into m_spawnedEffect (+0x30), then reads m_linkToTransform (+0x28): a copied EggSack linked effect plays from the shared pool."),
        new DamageWindowCheck("model-limb.send-destroy-master", 0x1380FAD, 0x150,
            new[]
            {
                "mov rax,[rax+0B8h]",
                "cmp byte ptr [rax+0B9h],0",
                "je near ptr 00000001813810C1h",
                "mov rcx,[rdi+130h]",
                "mov rax,[r8+3F0h]",
                "mov r8,[r8+3F8h]",
                "call rax",
                "jmp near ptr 0000000181DDC1F0h",
            },
            "Dam_EnemyDamageBase.SendDestroyLimb (RVA 0x1380F10) runs only when SNet's master flag ([static+0xB8]+0xB9) is set, sends m_destroyLimbPacket (+0x130) and destroys the limb locally through slot 44 (class + 0x3F0); a non-master caller reaches Debug.LogError instead."),
        new DamageWindowCheck("model-limb.receive-destroy-index", 0x137F040, 0x6D,
            new[]
            {
                "mov rax,[rcx+0F8h]",
                "movzx ecx,byte ptr [rdx]",
                "cmp ecx,[rax+18h]",
                "jae short 000000018137F0ADh",
                "mov rcx,[rax+rcx*8+20h]",
                "mov rax,[r8+3F0h]",
                "call rax",
            },
            "Dam_EnemyDamageBase.ReceiveDestroyLimb (RVA 0x137F040) indexes DamageLimbs (+0xF8) with the packet's limb byte, throws past the array length, and calls that limb's DestroyLimb (slot 44): a client breaks the same part only when its array matches the host's."),
    };

    internal static void VerifyDamageWindow(string path, string hash, Action<string, bool, string> check)
    {
        if (hash != SupportedHash)
        {
            check("damage.window.unsupported", false, "No disassembly attempted for an unreviewed native build.");
            return;
        }
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        foreach (var entry in DamageWindow.Concat(ModelLimbWindow))
        {
            var bytes = pe.GetSectionData(entry.WindowRva).GetContent(0, entry.WindowBytes).ToArray();
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes));
            decoder.IP = pe.PEHeaders.PEHeader!.ImageBase + (uint)entry.WindowRva;
            ulong end = decoder.IP + (ulong)entry.WindowBytes;
            var lines = new List<string>();
            while (decoder.IP < end)
            {
                var instruction = decoder.Decode();
                if (instruction.Code == Code.INVALID) break;
                lines.Add(instruction.ToString());
            }
            int cursor = 0;
            foreach (var expected in entry.Instructions)
            {
                int found = lines.FindIndex(cursor, line => line == expected);
                if (found >= 0) cursor = found + 1;
                else { cursor = -1; break; }
            }
            check(entry.Id, cursor >= 0, entry.Detail);
        }
    }

    internal static void Verify(string path, string hash, Action<string, bool, string> check)
    {
        if (hash != SupportedHash)
        {
            check("native.health.unsupported", false, "No disassembly attempted for an unreviewed native build.");
            return;
        }
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        string[] Decode(int rva, int length)
        {
            var bytes = pe.GetSectionData(rva).GetContent(0, length).ToArray();
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes));
            decoder.IP = pe.PEHeaders.PEHeader!.ImageBase + (uint)rva;
            ulong end = decoder.IP + (uint)length;
            var lines = new List<string>();
            while (decoder.IP < end) lines.Add(decoder.Decode().ToString());
            return lines.ToArray();
        }
        var send = Decode(ReviewedRvas["System.Void Dam_SyncedDamageBase::SendSetHealth(System.Single)"], 0x150);
        var receive = Decode(ReviewedRvas["System.Void Dam_EnemyDamageBase::ReceiveSetHealth(pSetHealthData)"], 0x30);
        check("native.health.host-check", send.Any(l => l.Contains("cmp byte ptr [rax+0B9h],0")),
            "Build-specific SendSetHealth host-check instruction; not proof of multiplayer behavior.");
        check("native.health.send-receive", send.Any(l => l.Contains("call 0000000181633510h")) && send.Any(l => l == "call rax"),
            "Build-specific packet-send and virtual local-receive call shapes at SendSetHealth RVA 0x161F790.");
        check("native.health.readback", receive.Any(l => l.Contains("call 0000000181BB5AE0h"))
            && receive.Any(l => l.Contains("movss dword ptr [rbx+20h],xmm0")),
            "ReceiveSetHealth RVA 0x1380D50 decodes SFloat16 then stores the health field; no method was executed.");
    }
}
