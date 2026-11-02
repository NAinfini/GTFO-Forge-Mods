using System.Text.Json;
using ForgeOrdnance;
using SNetwork;
using UnityEngine;

internal static class ApiChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const string type = "test.Retained";
        var receiver = new Receiver();
        check(OrdnanceApi.RegisterPayload(type, receiver), "extension registers a payload type through the public registration API");
        OrdnanceApi.Retained.Clear();
        OrdnanceNetwork.Broadcasts.Clear();
        SNet.IsMaster = true;
        for (uint id = 1; id <= OrdnanceApi.MaxKeptStates; id++)
            if (!OrdnanceApi.SendState(type, 1, id, 0, new byte[] { 1 })) throw new Exception("premature retained-state refusal");
        check(receiver.Received == OrdnanceApi.MaxKeptStates && OrdnanceNetwork.Broadcasts.Count == receiver.Received,
            "accepted retained states reach both host callback and peers");
        check(!OrdnanceApi.SendState(type, 1, 999, 0, new byte[] { 2 }) && receiver.Received == OrdnanceApi.MaxKeptStates
            && OrdnanceNetwork.Broadcasts.Count == receiver.Received && OrdnanceApi.Retained.Count == OrdnanceApi.MaxKeptStates,
            "full retained capacity refuses the state without publishing a live-only object");
        check(OrdnanceApi.SendState(type, 1, 1, 0, new byte[] { 3 }) && receiver.Last == 3,
            "existing retained state remains replaceable at capacity");
        OrdnanceApi.ClearState(type, 1, 1);
        check(OrdnanceApi.SendState(type, 1, 999, 0, new byte[] { 4 })
            && OrdnanceApi.Retained.InOrder().Last().ToBytes()!.Single() == 4,
            "clearing a finished call releases capacity for a new replayable state");
        OrdnanceApi.SendData(type, 1, 1000, 0, new byte[] { 5 });
        check(receiver.Last == 5 && OrdnanceApi.Retained.Count == OrdnanceApi.MaxKeptStates,
            "transient data remains independent of retained capacity");
        OrdnanceApi.Retained.Clear();
        OrdnanceNetwork.Broadcasts.Clear();
        CallBlockChecks(check);
        AppearanceChecks(check);
    }

    private static void CallBlockChecks(Action<bool, string> check)
    {
        var first = OrdnanceApi.BlockCalls("first");
        var second = OrdnanceApi.BlockCalls("second");
        check(OrdnanceApi.CallsBlocked && first.Active && second.Active, "a call block holds calls off while it exists");
        first.Dispose();
        check(OrdnanceApi.CallsBlocked && !first.Active && second.Active, "disposing one block leaves the others holding");
        first.Dispose();
        check(OrdnanceApi.CallsBlocked, "disposing a block twice is safe and releases nothing else");
        second.Dispose();
        check(!OrdnanceApi.CallsBlocked, "calls work again once every block is disposed");
    }

    private static void AppearanceChecks(Action<bool, string> check)
    {
        var model = new ForgeVisual.ModelHandle();
        foreach (var name in new[] { "door", "weapon1", "weapon2" }) model.Prefab.transform.Children.Add(name, new Transform());
        var doors = new[] { new OrdnancePodDoor("door", new Vector3(0, 2, 0), -105) };
        var size = new Vector3(.91f, 1.87f, .41f);
        var bays = new[] { new OrdnancePodSocket("weapon1", size, Quaternion.identity), new OrdnancePodSocket("weapon2", size, Quaternion.identity) };
        var appearance = new OrdnancePodAppearance(model, doors, bays);
        doors[0] = default; bays[0] = default;
        check(appearance.WeaponBayCount == 2 && appearance.PickupBayCount == 0 && appearance.Doors[0].Degrees == -105
            && appearance.Doors[0].Axis.y == 1 && appearance.WeaponBays[0].Path == "weapon1",
            "custom pod owns immutable door motions and two full-size bays, independent of caller arrays");
        bool Refuses(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
        check(Refuses(() => new OrdnancePodAppearance(model, new[] { new OrdnancePodDoor("missing", Vector3.up, 90) }, appearance.WeaponBays)),
            "custom pod refuses missing model transforms before spawning a delivery");
        check(Refuses(() => new OrdnancePodDoor("door", default, 90)) && Refuses(() => new OrdnancePodDoor("door", Vector3.up, float.NaN))
            && Refuses(() => new OrdnancePodSocket("weapon1", new Vector3(1, -1, 1), Quaternion.identity))
            && Refuses(() => new OrdnancePodSocket("weapon1", size, default)),
            "custom pod rejects undefined motion, non-finite angles, invalid bay sizes and rotations");
        check(Refuses(() => new OrdnancePodAppearance(model, appearance.Doors, Array.Empty<OrdnancePodSocket>()))
            && Refuses(() => new OrdnancePodAppearance(model, appearance.Doors, new OrdnancePodSocket[1])),
            "custom pod refuses empty and uninitialized weapon bay descriptors");
    }

    private sealed class Receiver : IOrdnancePayload
    {
        internal int Received;
        internal byte Last;
        public object Parse(JsonElement payload) => 0;
        public void Execute(OrdnanceExecution execution) { }
        public void OnData(OrdnancePayloadData data) { Received++; Last = data.Data.Single(); }
    }
}
