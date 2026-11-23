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
                "Maximum number of nearby wind effects to keep. Moving selections refresh in batches of 16 candidates per update, so after you move, the nearest wind effects are found again over a few frames; population changes rebuild once."),
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
            DroppedMagazineCleanup = Bool("DroppedMagazineCleanup", true,
                "Put a magazine dropped by a reload back into its gun's pool DroppedMagazineSeconds after it falls, so it disappears from the floor. Reload, ammunition and sound are unchanged; magazines other players drop are cleared on this machine too."),
            DroppedMagazineSeconds = Float("DroppedMagazineSeconds", 3f, 0.5f, 60f,
                "Seconds a dropped magazine stays on the floor before it disappears."),
            SpentFogRepellerCleanup = Bool("SpentFogRepellerCleanup", true,
                "Stop drawing a thrown fog repeller on this machine once it has stopped repelling fog. Only the model's drawing changes: the item, its collision and its multiplayer state stay as in the game, and other players are not affected. Repellers still working and the large carried fog repeller are untouched. Used glow sticks are already removed by the game."),
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
            IrfPhysicsHalfRate = Bool("IrfPhysicsHalfRate", true,
                "Compute the springing and swaying of instanced plants every other frame instead of every frame; the sway keeps its speed. Plants sway and react to you less smoothly, and plants at the screen edge can appear one frame late when you turn quickly. Plants have no collision, so nothing else changes."),
            TentacleLimitErrorFold = Bool("TentacleLimitErrorFold", true,
                "When too many striker tentacles attack at once, the game cannot draw the extra ones and writes the same error again and again. Write the first one of a level and, when the level ends, the count instead. The tentacles themselves are unchanged."),
            RagdollNodeRefresh = Bool("RagdollNodeRefresh", true,
                "When a dead enemy's ragdoll comes to rest, let the game look up again which room it lies in. Fixes corpses that vanish when the ragdoll was thrown across a room boundary. Nothing else changes."),
            ClosedDoorDepthLimit = Bool("ClosedDoorDepthLimit", true,
                "Behind any closed door (weak, security, Apex and bulkhead doors), draw only the first ClosedDoorDepth rooms instead of every room the door leads on to. The game never uses closed doors to hide what is behind them. Rooms you can also see another way, and damaged or destroyed doors, are drawn as usual. This can change play: a far doorway in a room seen through a door gap or window may show nothing behind it, including enemies you could otherwise spot from there."),
            ClosedDoorDepth = Int("ClosedDoorDepth", 2, 1, 4,
                "Rooms drawn behind a closed door when ClosedDoorDepthLimit is on. 1 draws only the room right behind the door."),
            TextureStreamingOff = Bool("TextureStreamingOff", true,
                "Turn off the game's texture streaming manager during a level when that level has no streaming textures, so its per-frame renderer walk stops. Nothing on screen changes; if a level has streaming textures the option does nothing there."),
            JobWorkerLimit = Int("JobWorkerLimit", 0, 0, 128,
                "Number of Unity job worker threads allowed to run during a level (0 leaves the game's own count, one per core less one). Fewer workers wake fewer threads for each batch of jobs and take that work off the main thread; the work itself (animation, skinning, physics, culling) is then shared by fewer cores. Changed when a level starts and put back when it ends. Has no effect on the picture."),
            VoiceRestartGuard = Bool("VoiceRestartGuard", true,
                "Limit how often the voice chat restarts the microphone. A restart that follows the previous one within seconds waits 0.25 s, then twice as long each time up to 5 s, and the wait ends after 10 quiet seconds; a working microphone restarts exactly as in the game. While the guard holds a restart back, your voice may be missing for up to 5 s after a device change."),
            VoiceFrameSkipFilter = Bool("VoiceFrameSkipFilter", true,
                "Do not restart the voice chat's microphone because of a frame that took less than VoiceFrameSkipSeconds. The game restarts it after any frame over a very short limit, and that restart (stop and start of the microphone, 100 to 180 ms) is a second hitch right after the first. A longer frame still restarts it as in the game. Risk: after a hitch of 0.2 to 0.5 s the voice may carry a short glitch instead of being reset; not tested in multiplayer voice."),
            VoiceFrameSkipSeconds = Float("VoiceFrameSkipSeconds", 0.5f, 0.1f, 5f,
                "A frame shorter than this many seconds no longer restarts the microphone when VoiceFrameSkipFilter is on."),
            LightningPreload = Bool("LightningPreload", true,
                "Load the lightning animation curves while a level is loading instead of when the first lightning strikes (the game loads them with a blocking read that can stall a frame for over a second). Nothing on screen changes."),
            ShadowProxyHold = Bool("ShadowProxyHold", true,
                "Keep the shadow of an enemy switched on while a light keeps drawing it, instead of the game's switch-off and switch-on once per second. Nothing on screen changes; it only removes that flip's cost."),
            SetupRendererLookup = Bool("SetupRendererLookup", true,
                "When an enemy is set up, look it up in this mod's own corpse records instead of walking its renderers twice. Nothing on screen changes; it only removes this mod's own work on every spawn."),
            SpawnFrameDefer = Bool("SpawnFrameDefer", true,
                "On a frame where an enemy was just spawned, move this mod's own periodic work (corpse hiding, decal and debris cleanup, level-start queue) to the next frame. Nothing on screen changes."),
            FarSkinQuality = Bool("FarSkinQuality", true,
                "Enemies farther than FarSkinDistance blend each vertex from two bones instead of four and write no skinned motion vectors. Visible change: far enemy meshes deform more coarsely, and with TAA far enemies can smear slightly. Local to this machine."),
            FarSkinDistance = Float("FarSkinDistance", 20f, 5f, 200f,
                "Distance in metres from the local player beyond which FarSkinQuality applies."),
            ClientHiddenSyncThrottle = Bool("ClientHiddenSyncThrottle", false,
                "On a client, move enemies that are out of sight and farther than HiddenSyncDistance to their synced position only every HiddenSyncInterval frames. Changes play: such an enemy's position, and its colliders with it, lag behind the host, which can matter for shots or explosions through walls. Clients only; the host is never affected."),
            HiddenSyncDistance = Float("HiddenSyncDistance", 30f, 5f, 200f,
                "Distance in metres from the local player beyond which ClientHiddenSyncThrottle applies."),
            HiddenSyncInterval = Int("HiddenSyncInterval", 4, 2, 10,
                "Frames between position updates of a throttled enemy on a client."),
            UpdateBudgets = Bool("UpdateBudgets", false,
                "Use the enemy update budgets below instead of the game's. Changes play: enemies react, detect and move on a different schedule. Host only. Off leaves the game's budgets untouched."),
            BudgetBehaviour = Budget("BudgetBehaviour", "5,2,1", "Behaviour updates per frame for close, near and far enemies."),
            BudgetDetection = Budget("BudgetDetection", "10,4,1", "Detection updates per physics step for close, near and far enemies."),
            BudgetNetwork = Budget("BudgetNetwork", "12,3,1", "Network state updates per frame for close, near and far enemies."),
            BudgetLocomotion = Budget("BudgetLocomotion", "20,10,5", "Movement updates per frame for close, near and far enemies."),
            BudgetFixedLocomotion = Budget("BudgetFixedLocomotion", "5,2,1", "Physics-step movement updates for close, near and far enemies."),
            BudgetNodes = Budget("BudgetNodes", "1", "Course nodes whose enemies are re-sorted by distance per frame."),
            AvoidanceQuality = Bool("AvoidanceQuality", false,
                "Use AvoidanceType for enemy crowd avoidance instead of the game's. The game already uses low quality, so only 0 (none) is cheaper. Changes play: no avoidance lets enemies pass through each other and changes how groups bunch up in doorways. Host only. Enemies already walking switch at once. Off leaves the game's value untouched."),
            AvoidanceType = Int("AvoidanceType", 1, 0, 4,
                "Crowd avoidance quality when AvoidanceQuality is on: 0 none, 1 low (the game's own value), 2 medium, 3 good, 4 high."),
            PathfindingIterations = Bool("PathfindingIterations", true,
                "Use PathfindingIterationsPerFrame for Unity's path search budget instead of the game's. Changes play: a lower value spreads path searches over more frames, so enemies can start moving later. Host only. Off leaves the game's value untouched."),
            PathfindingIterationsPerFrame = Int("PathfindingIterationsPerFrame", 50, 1, 10000,
                "Path search iterations Unity runs per frame when PathfindingIterations is on."),
            Probe = Bool("Probe", false,
                "Developer measurement: write one line per ProbeSeconds with frame time, main and render thread time, and the time and count of enemy spawns, wake-ups, navigation switches, shadow redraws, per-enemy updates and shooter projectiles, to BepInEx/PerformanceLogs/ForgePerformance_*.log. Also installs the hooks of every option above so they can be switched during one session. Off costs nothing."),
            ProbeSeconds = Float("ProbeSeconds", 1f, 0.25f, 10f,
                "Seconds per probe line.")
        };
        string Budget(string key, string value, string description)
        {
            var effective = config.Bind(Section, key, value, description).Value;
            Write(key, effective);
            return effective;
        }
    }
}
