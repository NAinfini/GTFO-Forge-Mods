using ForgeOrdnance;

/// <summary>Developer drops: the <c>Drops</c> entries of <c>Settings.json</c>, their place on the level, the level event that fires
/// them, the two arrival modes, and how far the call-menu switch reaches.</summary>
internal static class DropChecks
{
    private sealed class FakeLevel : IDropLevel
    {
        public bool HasDimension(int dimension) => dimension is 0 or 1;
        public bool TryZone(int dimension, int layer, int localIndex, out int areaCount)
        {
            areaCount = 3;
            return dimension == 0 && layer == 0 && localIndex == 2;
        }
        public Vec3 AreaCentre(int dimension, int layer, int localIndex, int area) => new(10 * area, -5, 100);
    }

    internal static void Run(Action<bool, string> check)
    {
        // ---- the arrival mode and its stages
        check(ArrivalPlan.Stages(OrdnanceArrivalMode.Drop).SequenceEqual(new[] { ArrivalStage.Vehicle, ArrivalStage.Contents })
            && ArrivalPlan.Stages(OrdnanceArrivalMode.Direct).SequenceEqual(new[] { ArrivalStage.Contents }),
            "arrival stages: a drop plays the vehicle then the contents, a direct arrival only the contents");
        check(ArrivalPlan.ContentsAfter(OrdnanceArrivalMode.Drop, 4.6f) == 4.6f && ArrivalPlan.ContentsAfter(OrdnanceArrivalMode.Direct, 4.6f) == 0f,
            "arrival stages: the contents wait for the vehicle only when there is one");
        check(ArrivalPlan.TryParse("Drop", out var drop) && drop == OrdnanceArrivalMode.Drop && ArrivalPlan.TryParse("Direct", out var direct) && direct == OrdnanceArrivalMode.Direct
            && !ArrivalPlan.TryParse("direct", out _) && !ArrivalPlan.TryParse(null, out _), "Arrival accepts exactly Drop and Direct");

        // ---- parsing
        var errors = new List<string>();
        var settings = OrdnanceSettings.Parse(@"{
            ""Levels"": { ""R5A2"": { ""CallMenu"": false, ""Drops"": [
                { ""Name"": ""start-suit"", ""Ordnance"": ""t.suit"", ""When"": ""LevelStart"", ""Arrival"": ""Direct"", ""Delay"": 3, ""Yaw"": 90,
                  ""Where"": { ""DimensionIndex"": 0, ""Layer"": 0, ""LocalIndex"": 2, ""Area"": 1, ""Offset"": { ""x"": 1, ""y"": 0, ""z"": -2 } } },
                { ""Name"": ""door-supply"", ""Ordnance"": ""t.supply"", ""When"": ""Event"",
                  ""Where"": { ""DimensionIndex"": 0, ""Layer"": 0, ""LocalIndex"": 2, ""Position"": { ""x"": 4.5, ""y"": -3, ""z"": 12 } } },
                { ""Name"": ""anywhere"", ""Ordnance"": ""t.supply"", ""When"": ""Event"" }
            ] } } }", errors);
        check(settings != null && errors.Count == 0, "drops: a valid list parses: " + string.Join(" | ", errors));
        var level = settings!.For("R5A2");
        var start = level.Drops[0];
        check(level.Drops.Count == 3 && start.When == DropWhen.LevelStart && start.Arrival == OrdnanceArrivalMode.Direct && start.Delay == 3 && start.Yaw == 90
            && start.Where is { Dimension: 0, Layer: 0, LocalIndex: 2, Point: { Area: 1, X: 1, Y: 0, Z: -2 } }, "drops: a level-start drop keeps its timing, facing and area place");
        var onDoor = level.Drops[1];
        check(onDoor.When == DropWhen.Event && onDoor.Arrival == OrdnanceArrivalMode.Drop && onDoor.Where!.Point.IsWorld && onDoor.Where.Point.X == 4.5f,
            "drops: Arrival defaults to Drop and a world Position is kept as given");
        check(level.Drops[2].Where == null, "drops: an event drop may leave its place to the event");
        check(settings.For("Other").Drops.Count == 0 && settings.For("Other").CallMenu, "drops and the call-menu switch of one level do not reach other levels");

        void Refused(string name, string drops, string field)
        {
            var bad = new List<string>();
            var parsed = OrdnanceSettings.Parse("{\"Drops\":[" + drops + "]}", bad);
            check(parsed == null && bad.Count >= 1 && bad.Exists(e => e.Contains(field)), "drops refuse " + name + " (" + field + "): " + string.Join(" | ", bad));
        }
        const string here = "\"Where\":{\"DimensionIndex\":0,\"Layer\":0,\"LocalIndex\":0,\"Area\":0}";
        Refused("a missing name", "{\"Ordnance\":\"t.a\",\"When\":\"LevelStart\"," + here + "}", "Name");
        Refused("an unknown timing", "{\"Name\":\"a\",\"Ordnance\":\"t.a\",\"When\":\"OnDoor\"," + here + "}", "When");
        Refused("an unknown arrival", "{\"Name\":\"a\",\"Ordnance\":\"t.a\",\"When\":\"LevelStart\",\"Arrival\":\"Teleport\"," + here + "}", "Arrival");
        Refused("a level-start drop without a place", "{\"Name\":\"a\",\"Ordnance\":\"t.a\",\"When\":\"LevelStart\"}", "Where");
        Refused("a delay on an event drop", "{\"Name\":\"a\",\"Ordnance\":\"t.a\",\"When\":\"Event\",\"Delay\":2}", "Delay");
        Refused("both Position and Area", "{\"Name\":\"a\",\"Ordnance\":\"t.a\",\"When\":\"Event\",\"Where\":{\"DimensionIndex\":0,\"Layer\":0,\"LocalIndex\":0,\"Area\":0,\"Position\":{\"x\":0,\"y\":0,\"z\":0}}}", "Where");
        Refused("a layer the game does not have", "{\"Name\":\"a\",\"Ordnance\":\"t.a\",\"When\":\"Event\",\"Where\":{\"DimensionIndex\":0,\"Layer\":3,\"LocalIndex\":0,\"Area\":0}}", "Layer");
        Refused("an Offset with a world Position", "{\"Name\":\"a\",\"Ordnance\":\"t.a\",\"When\":\"Event\",\"Where\":{\"DimensionIndex\":0,\"Layer\":0,\"LocalIndex\":0,\"Position\":{\"x\":0,\"y\":0,\"z\":0},\"Offset\":{\"x\":0,\"y\":0,\"z\":0}}}", "Offset");
        Refused("a duplicate name", "{\"Name\":\"a\",\"Ordnance\":\"t.a\",\"When\":\"Event\"},{\"Name\":\"a\",\"Ordnance\":\"t.a\",\"When\":\"Event\"}", "Name");
        Refused("an unknown field", "{\"Name\":\"a\",\"Ordnance\":\"t.a\",\"When\":\"Event\",\"Count\":2}", "Count");

        // ---- checked against the loaded call-ins
        var registry = new PayloadRegistry();
        registry.Register("Stub", new StubParser());
        registry.Register("Strike", new StubParser());
        var categories = new CategoryRegistry(id => () => id);
        string Def(string id, string type) => "{\"Id\":\"" + id + "\",\"Category\":\"Supply\",\"Code\":[\"Up\"],\"Cooldown\":1,\"CallInDelay\":1,\"Payload\":{\"Type\":\"" + type + "\",\"Count\":1}}";
        var set = DefinitionSet.Build(DefinitionParser.ParseFile("d.json", "[" + Def("t.supply", "Stub") + "," + Def("t.strike", "Strike") + "]", registry, categories).Definitions, new List<string>());
        var listErrors = new List<string>();
        var raw = OrdnanceSettings.Parse(@"{ ""Drops"": [
            { ""Name"": ""ok"", ""Ordnance"": ""t.supply"", ""When"": ""Event"", ""Arrival"": ""Direct"" },
            { ""Name"": ""missing"", ""Ordnance"": ""t.none"", ""When"": ""Event"" },
            { ""Name"": ""no-direct"", ""Ordnance"": ""t.strike"", ""When"": ""Event"", ""Arrival"": ""Direct"" },
            { ""Name"": ""strike-drop"", ""Ordnance"": ""t.strike"", ""When"": ""Event"" } ],
            ""Levels"": { ""A"": { ""CallMenu"": false }, ""B"": {} } }", listErrors)!;
        var usable = raw.WithUsableDrops(set, type => type == "Stub", listErrors);
        check(usable.For("A").Drops.Select(d => d.Name).SequenceEqual(new[] { "ok", "strike-drop" }) && usable.For("B").Drops.Count == 2,
            "drops: an unloaded call-in and a direct arrival its payload cannot do are left out; the rest stay, in every level that inherits them");
        check(listErrors.Count == 2 && listErrors.Exists(e => e.Contains("'missing'") && e.Contains("not a loaded call-in"))
            && listErrors.Exists(e => e.Contains("'no-direct'") && e.Contains("cannot arrive Direct")),
            "drops: each skipped drop is one error naming it and why, reported once although two levels inherit the list: " + string.Join(" | ", listErrors));
        check(!usable.For("A").CallMenu && usable.For("B").CallMenu, "the call-menu switch survives the drop check per level");

        // ---- place on the level
        var world = new FakeLevel();
        check(DropPlacement.Resolve(start.Where!, world, out var point) == null && point.X == 11 && point.Y == -5 && point.Z == 98,
            "place: an area place is the area's centre moved by the offset");
        check(DropPlacement.Resolve(onDoor.Where!, world, out point) == null && point.X == 4.5f && point.Y == -3 && point.Z == 12, "place: a world position is used as written");
        var noDimension = DropPlacement.Resolve(new DropWhere(5, 0, 2, new DropPoint(0, 0, 0, 0)), world, out _);
        var noZone = DropPlacement.Resolve(new DropWhere(1, 0, 2, new DropPoint(0, 0, 0, 0)), world, out _);
        var noArea = DropPlacement.Resolve(new DropWhere(0, 0, 2, new DropPoint(3, 0, 0, 0)), world, out _);
        check(noDimension != null && noDimension.Contains("dimension 5 does not exist") && noZone != null && noZone.Contains("no zone 2")
            && noArea != null && noArea.Contains("has 3 area(s); Area 3 does not exist"), "place: a missing dimension, zone or area is refused with its own reason");

        // ---- the level event
        check(DeveloperDrops.Fires(DeveloperDrops.EventType, 1, 1, false, 4f, 0f, out var wait) && wait == 4f, "event: a matching trigger fires after the event's Delay");
        check(!DeveloperDrops.Fires(DeveloperDrops.EventType, 2, 1, false, 0f, 0f, out _) && DeveloperDrops.Fires(DeveloperDrops.EventType, 2, 1, true, 0f, 0f, out _),
            "event: another trigger does not fire unless triggers are ignored (a terminal command's events)");
        check(DeveloperDrops.Fires(DeveloperDrops.EventType, 0, 0, true, 10f, 6f, out wait) && wait == 4f && !DeveloperDrops.Fires(DeveloperDrops.EventType, 0, 0, true, 5f, 6f, out _),
            "event: inside a running duration only a delay still ahead fires, for the time left");
        check(!DeveloperDrops.Fires(13, 0, 0, true, 0f, 0f, out _), "event: other event types are not drops");

        // ---- the call-menu switch
        var book = new SlotBook();
        var origin = new Vec3(0, 0, 0);
        check(ThrowValidator.Evaluate(book, true, false, 1, 7, origin, origin, 0, 80, () => true) == Rejection.CallMenuOff
            && ThrowValidator.Evaluate(book, true, true, 1, 7, origin, origin, 0, 80, () => true) != Rejection.CallMenuOff,
            "call menu off: the host refuses every throw, whatever the loadout");
        var wire = HostRulesWire.Encode(HostRulesSnapshot.From("R5A2", set, level));
        check(HostRulesWire.TryDecode(wire, out var rules) && rules != null && !rules.CallMenu && !rules.ToSettings(set).CallMenu,
            "call menu off: the switch reaches every player through the host's rules");
    }
}
