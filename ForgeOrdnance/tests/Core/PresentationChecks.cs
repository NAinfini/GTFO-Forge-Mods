using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ForgeOrdnance;

/// <summary>The call-in presentation on ForgeVisual: the beacon's animation numbers, the particle budgets paired with the baked prefabs,
/// and that nothing in the plugin draws or loads on its own any more.</summary>
internal static class PresentationChecks
{
    internal static void Run(Action<bool, string> check, [CallerFilePath] string here = "")
    {
        var mod = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../.."));
        static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-3f;

        var calm = BeaconShow.At(2f, 20f, 30f);
        check(Near(calm.Grow, 1f) && Near(calm.Urgency, 0f) && Near(calm.Flash, 0f) && Near(calm.Width, 1f) && Near(calm.PingRate, 0.62f)
            && Near(calm.MarkRemaining, 20f / 30f) && Near(calm.MarkOpacity, 1f) && calm.CoreLevel is >= 0.85f and <= 1.0001f,
            "beacon: a waiting beacon breathes at full size, pings 0.62 times a second and shows the time left as a fraction");
        var tight = BeaconShow.At(30f, 1.5f, 30f);
        check(Near(tight.Urgency, 0.5f) && Near(tight.PingRate, 1.1f + 1.9f * 0.5f) && tight.CoreLevel > calm.CoreLevel && tight.GlowWidthScale < calm.GlowWidthScale,
            "beacon: the last seconds speed the pings up from 1.1 to 3 a second, brighten the core and narrow the glow");
        var flash = BeaconShow.At(30f, 0.075f, 30f);
        check(Near(flash.Flash, 0.5f) && Near(flash.Width, 2.5f) && flash.CoreLevel <= 1.6f && flash.HotToWhite > 0.5f && flash.FlareLevel > 0.6f,
            "beacon: at arrival the whole beam flashes (wider, hotter, flare brighter) and the core stays within 1.6");
        var start = BeaconShow.At(0f, 30f, 30f);
        check(Near(start.Grow, 0f) && Near(start.Width, 0f) && Near(start.CoreLevel, 0f) && Near(start.GlowLevel, 0f), "beacon: a new beacon fades in from nothing over 0.3 s");
        check(Near(BeaconShow.Rise(0f, true), 0f) && Near(BeaconShow.Rise(0.15f, true), 0.75f) && Near(BeaconShow.Rise(5f, true), 1f) && Near(BeaconShow.Rise(0f, false), 1f),
            "beacon: a fresh beam shoots up over 0.3 s (eased out); a replayed one is already up");

        // Every sentry the Gear payload stands is registered with its own look, whichever of the marker hook and the host's spawn callback runs first.
        var gear = File.ReadAllText(Path.Combine(mod, "Native/Payloads/GearPayload.cs"));
        check(gear.Contains("Register(sentry, identity, !pickable, OrdnanceTurrets.KeyOf(look))") && !gear.Contains("Register(sentry, identity);"),
            "turret look: the host's spawn callback registers the sentry with its look and pickup lock, not as an unmarked sentry");

        // Every way a beacon ends gives its beam back: arrival, the late sweep, a level clear and a failed build all go through Destroy.
        var beacon = File.ReadAllText(Path.Combine(mod, "Native/Presentation.cs"));
        var controller = File.ReadAllText(Path.Combine(mod, "Native/OrdnanceController.cs"));
        check(Regex.IsMatch(beacon, @"internal void Destroy\(\)\s*\{[^}]*_beam\?\.Release\(\);[^}]*_mark\?\.Remove\(\);") && Regex.IsMatch(beacon, @"catch\s*\{[^}]*Destroy\(\);\s*throw;"),
            "beacon: destroying a beacon releases its beam and ground mark, and a beacon that fails to build destroys itself");
        check(controller.Split("beacon.Destroy()").Length == 3 && controller.Split("_beacons[i].Destroy()").Length == 2,
            "beacon: arrival, the late sweep and the level clear each destroy the beacons they drop");

        // The prefab's maxParticles is the capacity the plugin passes to ForgeVisual: they are written in two places and must agree.
        var build = File.ReadAllText(Path.Combine(mod, "tools/OrdnanceBundleBuild.cs"));
        var fx = File.ReadAllText(Path.Combine(mod, "Native/Presentation/OrdnanceFx.cs"));
        var impact = File.ReadAllText(Path.Combine(mod, "Native/Delivery/PodImpactEffects.cs"));
        var baked = Regex.Match(build, @"FxLimits = \{([^}]*)\}").Groups[1].Value.Split(',').Select(s => int.Parse(s.Trim())).ToArray();
        var planned = new[] { "Flame", "Smoke", "Spark", "Flare", "Ring", "Rock" }.Select(n => int.Parse(Regex.Match(fx, n + @"Capacity = (\d+)").Groups[1].Value)).ToArray();
        check(baked.SequenceEqual(planned), "effects: the plugin's particle capacities equal the prefab's maxParticles (" + string.Join(",", baked) + " vs " + string.Join(",", planned) + ")");
        var bakedImpact = new[] { "dust-ring", "debris-puffs" }.Select(n => int.Parse(Regex.Match(build, "AddImpactParticles\\(root, \"" + n + "\", (\\d+),").Groups[1].Value)).ToArray();
        var plannedImpact = new[] { "Dust", "Debris" }.Select(n => int.Parse(Regex.Match(impact, n + @"Capacity = (\d+)").Groups[1].Value)).ToArray();
        check(bakedImpact.SequenceEqual(plannedImpact), "effects: the landing dust capacities equal the prefab's maxParticles");
        check(build.Contains("!particles.main.loop") && build.Contains("!prefab.activeSelf"), "effects: the baked prefabs are active with looping world-space systems, as ForgeVisual's particle pool needs");

        // One drawing stack: the plugin spawns models, particles, beams, marks, trails and glow only through ForgeVisual.
        var banned = new[] { "new Material(", "Shader.Find", "AssetBundle.", "LineRenderer", "CreatePrimitive", "new Texture2D(", "ParticleSystemRenderer" };
        var own = Directory.GetFiles(Path.Combine(mod, "Native"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => banned.Where(b => File.ReadAllText(file).Contains(b)).Select(b => Path.GetFileName(file) + ": " + b)).ToList();
        check(own.Count == 0, "presentation: nothing in the plugin builds its own materials, loads bundles or shaders, or draws lines (" + string.Join("; ", own) + ")");
        check(!Directory.GetFiles(Path.Combine(mod, "Native"), "*.Generated.cs", SearchOption.AllDirectories).Any() && !Directory.GetFiles(Path.Combine(mod, "tools"), "*.shader").Any(),
            "presentation: no embedded shader bundles or shader sources of its own");
    }
}