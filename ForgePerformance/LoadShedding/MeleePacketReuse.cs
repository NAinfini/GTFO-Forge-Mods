using System;
using System.Runtime.InteropServices;
using Gear;
using SNetwork;

namespace ForgePerformance.LoadShedding;

// The game registers a new network hit packet in the action manager for every first-person melee weapon
// setup and never drops one. The packet only plays hit sparks and sounds on other machines (damage travels on
// the enemy damage packets), and its receive action is static, so any instance receives for all of them.
// A packet is addressed on the wire by its replicator key and its position in that replicator, and every
// action packet registered later (revive state, communication menu) takes the next position. Taking a slot
// out therefore moves every later packet on this machine and on no other, which is the out-of-range and
// misrouting error seen when players with and without this option, or with another build, share a lobby. A
// handshake could not undo that once a slot is gone. So the slot stays: after a Setup the created packet's
// own slot is pointed at the first packet of the session and the weapon uses that one. The replicator keeps
// the length and order vanilla gives it, every index means what it means on every other machine, and the
// created packet, its byte buffer and delegates become garbage instead of living for the whole session.
internal static class MeleePacketReuse
{
    // SNet_Packet: <Replicator>k__BackingField (an interface reference, read as a raw pointer) and <Index>.
    internal const int PacketReplicator = 0x10;
    internal const int PacketIndex = 0x18;

    private static SNet_BroadcastAction<pMeleeHitPacketStruct>? _shared;
    private static long _setups, _reused, _adopted;

    internal static void Reset()
    {
        _shared = null; _setups = _reused = _adopted = 0;
    }

    internal static (long Setups, long Reused, long Adopted) Counts => (_setups, _reused, _adopted);

    internal static void AfterSetup(MeleeWeaponFirstPerson weapon)
    {
        var created = weapon.m_hitPacket;
        if (created == null) return;
        _setups++;
        if (_shared != null && SlotOf(_shared.m_packet) == null) _shared = null;

        var slot = SlotOf(created.m_packet);
        switch (ReusePolicy.DecideMeleePacket(created.Pointer, _shared?.Pointer ?? IntPtr.Zero, slot != null))
        {
            case ReusePolicy.MeleePacketAction.Adopt:
                _shared = created; _adopted++;
                break;
            case ReusePolicy.MeleePacketAction.Alias:
                var (packets, index) = slot!.Value;
                packets[index] = _shared!.m_packet;
                weapon.m_hitPacket = _shared;
                _reused++;
                break;
        }
    }

    // The packet list and index holding this packet, when its replicator is still one of the manager's and
    // still holds it at its own index (a new session registers every packet again).
    private static (Il2CppSystem.Collections.Generic.List<SNet_Packet> Packets, int Index)? SlotOf(SNet_Packet? packet)
    {
        var replicators = SNet.Actions?.m_replicators;
        if (packet == null || replicators == null) return null;
        var replicator = Marshal.ReadIntPtr(packet.Pointer, PacketReplicator);
        var index = Marshal.ReadByte(packet.Pointer, PacketIndex);
        for (var i = 0; i < replicators.Count; i++)
        {
            var candidate = replicators[i];
            if (candidate == null || candidate.Pointer != replicator) continue;
            var packets = candidate.GetPackets();
            return packets != null && index < packets.Count && packets[index].Pointer == packet.Pointer ? (packets, index) : null;
        }
        return null;
    }
}
