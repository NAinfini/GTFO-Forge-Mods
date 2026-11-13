using System;
using System.Globalization;
using BepInEx.Configuration;

namespace ForgePerformance.LoadShedding;

internal static class SheddingConfiguration
{
    private const string Section = "Performance";

    internal static SheddingSettings Read(ConfigFile config, Action<string> log)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (log is null) throw new ArgumentNullException(nameof(log));

        var summaryLog = config.Bind(Section, "SummaryLog", false,
            "Write one line per effective setting at start and one counter line per cut at level end to LogOutput.log. Off keeps the log to errors only.").Value;
        bool Bool(string key, bool value, string description)
        {
            var effective = config.Bind(Section, key, value, description).Value;
            Write(key, effective ? "true" : "false");
            return effective;
        }
        int Int(string key, int value, int minimum, int maximum, string description)
        {
            var effective = config.Bind(Section, key, value,
                new ConfigDescription(description, new AcceptableValueRange<int>(minimum, maximum))).Value;
            Write(key, effective.ToString(CultureInfo.InvariantCulture));
            return effective;
        }
        float Float(string key, float value, float minimum, float maximum, string description)
        {
            var effective = config.Bind(Section, key, value,
                new ConfigDescription(description, new AcceptableValueRange<float>(minimum, maximum))).Value;
            Write(key, effective.ToString("R", CultureInfo.InvariantCulture));
            return effective;
        }
        void Write(string key, string value)
        {
            if (summaryLog) log("type=performance_setting\tsection=" + Section + "\tkey=" + key + "\tvalue=" + value);
        }

        return new SheddingSettings
        {
            SummaryLog = summaryLog,
            CorpseShadows = Bool("CorpseShadows", true,
                "Turn off shadows cast by dead enemies. Corpses remain visible until the separate fast-hide option applies. Stealth (Shadow-type) enemies are not affected."),
            FastCorpseHide = Bool("FastCorpseHide", true,
                "Hide corpse renderers shortly after death. Corpses disappear sooner and stop colliding once hidden; enemy death and native despawn are unchanged."),
            CorpseLifetime = Float("CorpseLifetime", 0.01f, 0.01f, 60f,
                "Maximum seconds a corpse remains visible after its native decay timer starts. Does not change native despawn. The hidden corpse also stops colliding."),
            RagdollCap = Bool("RagdollCap", true,
                "Limit simultaneously moving enemy ragdolls. Older ragdolls stop moving when the limit is reached."),
            RagdollLimit = Int("RagdollLimit", 4, 1, 12,
                "Maximum number of enemy ragdolls allowed to move at once."),
            WindCapacityProtection = Bool("WindCapacityProtection", true,
                "Temporarily limit overlapping wind effects to the game's fixed capacity of 64 and restore them after rendering. Off returns to vanilla behaviour: more than 64 overlapping wind effects can make the game throw errors. WindNearest is a separate option."),
            WindNearest = Bool("WindNearest", true,
                "Keep wind motion on nearby decoration when many wind effects overlap. Distant plants may stop swaying in crowded scenes. Off removes this reduction; the separate WindCapacityProtection option controls overflow protection."),
            WindLimit = Int("WindLimit", 1, 1, 64,
                "Maximum number of nearby wind effects to keep. Moving selections refresh in batches of 64 candidates per update; population changes rebuild once."),
            GardenShadows = Bool("GardenShadows", true,
                "Skip costly extra plant and prop shadows in Garden areas. Geometry remains visible with fewer ambient shadows."),
            AmbientParticles = Bool("AmbientParticles", true,
                "Reduce ambient dust counts and update rate when AmbientDustOff is false. Dust looks sparser and less smooth."),
            AmbientDustOff = Bool("AmbientDustOff", true,
                "Completely turn off ambient air dust. Takes precedence over ambient dust thinning. Weapon, explosion, enemy and task effects stay unchanged."),
            VisorLiquidOff = Bool("VisorLiquidOff", true,
                "Hide all liquid on the visor, including blood, infection goo and water. Health and infection stay unchanged."),
            ParticleFactor = Float("ParticleFactor", 0.01f, 0.01f, 1f,
                "Fraction of the vanilla ambient particle count to keep."),
            ParticleHz = Int("ParticleHz", 1, 1, 15,
                "Maximum ambient particle simulation updates per second."),
            ShellEjection = Bool("ShellEjection", true,
                "Limit visual shell casings created per frame. Gunfire, hits and ammunition are unchanged; some casings may not appear."),
            ShellLimit = Int("ShellLimit", 6, 1, 128,
                "Maximum visual shell casings created in one frame."),
            DecorationShadows = Bool("DecorationShadows", true,
                "Skip shadows of confirmed visual decoration outside Garden areas. Gameplay objects keep their normal shadows."),
            CombatDecals = Bool("CombatDecals", true,
                "Limit and shorten blood and bullet-hole decals. Scans, markers and objective decals keep vanilla behavior."),
            DecalLimit = Int("DecalLimit", 1, 1, 4096,
                "Maximum number of blood and bullet-hole decals kept alive."),
            DecalLifetime = Float("DecalLifetime", 0.01f, 0.01f, 60f,
                "Maximum lifetime in seconds for blood and bullet-hole decals."),
            DecorationCulling = Bool("DecorationCulling", true,
                "Hide classified decoration beyond the configured camera distance and show it again nearby. Decorative objects may pop into view."),
            CullDistance = Float("CullDistance", 10f, 1f, 1000f,
                "Camera distance in metres beyond which classified decoration is hidden."),
            DecorationLights = Bool("DecorationLights", true,
                "Limit decorative ambient lights. Flashlights, alarms and task lights stay unchanged; dense ambient lighting may look dimmer."),
            LightLimit = Int("LightLimit", 12, 1, 32,
                "Maximum number of decorative lights visible at once."),
            DecorationAnimation = Bool("DecorationAnimation", true,
                "Stop off-screen skinning and animation only on classified decoration. It resumes when the decoration is visible."),
            ShadowDistance = Bool("ShadowDistance", true,
                "Shorten the shadow draw distance to ShadowDistanceFactor of the game's value, never below 20 metres. Far shadows disappear sooner."),
            ShadowDistanceFactor = Float("ShadowDistanceFactor", 0.65f, 0.3f, 1f,
                "Fraction of the game's shadow distance to keep."),
            SoftParticlesOff = Bool("SoftParticlesOff", true,
                "Turn off soft particle blending. Particles show hard edges where they meet walls and floors."),
            LeakCleanup = Bool("LeakCleanup", true,
                "Destroy or release what the game forgets after a level: empty text assets, effect lights, instanced-render buffers, flyer materials, terminal build-done callbacks, queued objective hint messages and duplicate menu header entries. Nothing visible changes."),
            DecayCleanup = Bool("DecayCleanup", true,
                "Clear the dissolve particles of corpses a few seconds after they finish. Already invisible; the cleanup never touches a corpse that is dissolving again."),
            DebrisFreeze = Bool("DebrisFreeze", true,
                "Freeze broken-object debris a few seconds after it lands. Debris no longer gets pushed around afterwards; it stops colliding."),
            GlueSoundRecycle = Bool("GlueSoundRecycle", true,
                "Return the sound player of a destroyed foam ball to the pool. Only the audio object is recycled; the ball itself is untouched."),
            StackTraceTrim = Bool("StackTraceTrim", true,
                "Stop Unity from writing stack traces for ordinary log and warning lines. Errors and exceptions keep their stack traces."),
            GuiMirrorOff = Bool("GuiMirrorOff", true,
                "Remove the mirror and reflection effect of the game's interface. The interface looks slightly flatter."),
            LowQualitySpitter = Bool("LowQualitySpitter", true,
                "Draw infection spitters with blurred textures and without their ground decal. Spitters look flatter; behaviour is unchanged."),
            CorpseRecycle = Bool("CorpseRecycle", true,
                "Shorten the game's corpse timer so a dead enemy's body is removed by the game's normal despawn sooner. This changes play: the body stops blocking bullets sooner."),
            CorpseRecycleSeconds = Float("CorpseRecycleSeconds", 1f, 0.1f, 60f,
                "Maximum seconds from death until the game despawns the body. A native timer that is already shorter is kept."),
            MeleePacketReuse = Bool("MeleePacketReuse", false,
                "Let every melee weapon of this player share one network hit packet, so the packet each new setup registers can be freed. The packet's slot is kept, so every network packet index stays the same as in the unmodded game and players with or without this mod or this option can share a lobby. Damage is unchanged."),
            IrfWorldCameraOnly = Bool("IrfWorldCameraOnly", true,
                "Draw the game's instanced decoration only into the main camera instead of every camera that renders its layer. Other cameras, such as a spectator camera, may lose the decoration."),
            IrfOffscreenSkip = Bool("IrfOffscreenSkip", true,
                "Skip the per-frame update and draw of static instanced decoration whose fixed bounds are fully outside the main camera view. Hidden plants stop swaying and their shadows use the last computed instances."),
            RagdollNodeRefresh = Bool("RagdollNodeRefresh", true,
                "When a dead enemy's ragdoll comes to rest, let the game look up again which room it lies in. Fixes corpses that vanish when the ragdoll was thrown across a room boundary. Nothing else changes."),
            ClosedDoorDepthLimit = Bool("ClosedDoorDepthLimit", true,
                "Behind any closed door (weak, security, Apex and bulkhead doors), draw only the first ClosedDoorDepth rooms instead of every room the door leads on to. The game never uses closed doors to hide what is behind them. Rooms you can also see another way, and damaged or destroyed doors, are drawn as usual. This can change play: a far doorway in a room seen through a door gap or window may show nothing behind it, including enemies you could otherwise spot from there."),
            ClosedDoorDepth = Int("ClosedDoorDepth", 2, 1, 4,
                "Rooms drawn behind a closed door when ClosedDoorDepthLimit is on. 1 draws only the room right behind the door."),
            TextureStreamingOff = Bool("TextureStreamingOff", true,
                "Turn off the game's texture streaming manager during a level when that level has no streaming textures, so its per-frame renderer walk stops. Nothing on screen changes; if a level has streaming textures the option does nothing there.")
        };
    }
}
