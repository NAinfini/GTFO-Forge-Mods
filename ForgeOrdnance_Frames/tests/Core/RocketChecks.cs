using ForgeOrdnanceFrames;

internal static class RocketChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var flights = new FrameRocketFlights<int>();

        // The largest legal travel time is 60 seconds and a legal salvo can add eight flights each second.
        for (var i = 0; i < 480; i++) flights.Add(i);
        check(flights.Count == 480 && flights.Capacity >= 480, "rocket flights grow past the old 32-flight limit without dropping grants");
        check(Enumerable.Range(0, 480).All(i => flights.IsLive(i) && flights.At(i) == i), "grown rocket storage preserves every flight");

        var oldHost = new FrameRocketFlights<int>();
        var newHost = new FrameRocketFlights<int>();
        var oldCopy = oldHost.Add(480);
        var newCopy = newHost.Add(480);
        // The launch happened while the first peer was host. Before impact, authority moves to the second peer.
        var oldOwnsDamage = oldHost.Settle(oldCopy, false);
        var newOwnsDamage = newHost.Settle(newCopy, true);
        check(!oldOwnsDamage && newOwnsDamage && oldHost.Count == 0 && newHost.Count == 0,
            "an in-flight rocket follows migrated authority at impact and both peers retire their copy");
        check(!newHost.Settle(newCopy, true), "a landed rocket cannot settle damage twice on one peer");

        flights.Reset();
        check(flights.Count == 0 && flights.Capacity == 32, "rocket flight storage resets at level cleanup");

        // One launcher of four tubes, a salvo of four, 15 s to reload: rocket i leaves tube i.
        check(FrameRocketRack.Tube(0, 4) == 0 && FrameRocketRack.Tube(3, 4) == 3 && FrameRocketRack.Tube(5, 4) == 1, "a salvo fires the tubes in turn");
        bool Loaded(int tube, float now) => FrameRocketRack.Loaded(tube, 4, 4, 15f, 10f, now);
        check(Enumerable.Range(0, 4).All(t => FrameRocketRack.Loaded(t, 4, 4, 15f, float.NegativeInfinity, 0f)), "every tube is loaded before the first salvo");
        check(Loaded(0, 9.9f) && !Loaded(0, 10f) && Loaded(1, 10.1f) && !Loaded(1, 10f + FrameRules.RocketInterval),
            "a tube empties the moment its rocket leaves, not before");
        check(FrameRocketRack.Loaded(2, 4, 2, 15f, 10f, 11f) && FrameRocketRack.Loaded(3, 4, 2, 15f, 10f, 11f), "tubes the salvo did not use stay loaded");
        check(!Loaded(0, 13.7f) && Loaded(0, 13.75f) && !Loaded(3, 24.9f) && Loaded(3, 25f),
            "the tubes reload in firing order over the cooldown, the last the moment the next salvo is ready");
        // More rockets than tubes: a tube fires twice and stays empty until its last rocket is reloaded.
        check(!FrameRocketRack.Loaded(0, 2, 4, 8f, 0f, 3f) && !FrameRocketRack.Loaded(0, 2, 4, 8f, 0f, 5.9f) && FrameRocketRack.Loaded(0, 2, 4, 8f, 0f, 6f),
            "a tube used twice in a salvo refills with its last rocket");
    }
}
