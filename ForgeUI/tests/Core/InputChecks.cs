using System.Collections;
using System.Reflection;
using CellMenu;
using ForgeUI;

internal static class InputChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var routes = (IDictionary)typeof(Input).GetField("Routes", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var items = Enumerable.Range(1, 25).Select(i => new CM_Item { Pointer = (IntPtr)i }).ToArray();
        foreach (var item in items) Input.Route(item, () => { }, null);
        check(routes.Count == 25, "routes registered");
        Input.Route(items[0], () => { }, null);
        check(routes.Count == 25, "rerouting pointer replaces registration");
        CM_Item.Probes = 0;
        Input.PruneDestroyed();
        check(CM_Item.Probes == 8 && routes.Count == 25, "live route sweep probes only eight");
        Input.Unroute(items[0].Pointer);
        check(!routes.Contains(items[0].Pointer) && routes.Count == 24, "explicit unroute is immediate");
        Input.Unroute(items[0].Pointer);
        check(routes.Count == 24, "repeated unroute harmless");
        Input.Unroute(items[^1].Pointer);
        check(!routes.Contains(items[^1].Pointer) && routes.Count == 23, "unroute moved last route by saved index");
        foreach (var item in items) item.Destroyed = true;
        var bounded = true;
        for (var frame = 0; frame < 3; frame++)
        {
            CM_Item.Probes = 0;
            Input.PruneDestroyed();
            bounded &= CM_Item.Probes <= 8;
        }
        check(bounded, "destroyed route sweeps stay bounded");
        check(routes.Count == 0, "destroyed routes eventually all removed across cursor wrap and swap");
        CM_Item.Probes = 0;
        Input.PruneDestroyed();
        check(CM_Item.Probes == 0, "empty route sweep does no liveness work");
        var replacement = new CM_Item { Pointer = items[0].Pointer };
        Input.Route(items[0], () => { }, null);
        Input.Route(replacement, () => { }, null);
        Input.PruneDestroyed();
        check(routes.Count == 1, "live pointer replacement survives dead predecessor sweep");
        Input.Unroute(replacement.Pointer);
        check(routes.Count == 0, "replacement can be explicitly removed after shrink");
    }
}
