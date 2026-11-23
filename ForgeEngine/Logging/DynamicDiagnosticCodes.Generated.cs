// Generated from catalog/authoring/diagnostic-codes.json by Tools/Forge/dynamic-diagnostic-codes.ts. Do not edit.
using System;
using System.Collections.Generic;

namespace ForgeEngine.Logging;

public static partial class DynamicDiagnosticCodes
{
    private static readonly Dictionary<string, HashSet<string>> Expansions = new(StringComparer.Ordinal)
    {
        ["content-{value}-{difference}"] = new HashSet<string>(StringComparer.Ordinal) { "content-audio-extra", "content-audio-files", "content-audio-mismatch", "content-audio-missing", "content-audio-unreadable", "content-enemies-extra", "content-enemies-files", "content-enemies-mismatch", "content-enemies-missing", "content-enemies-unreadable", "content-map-extra", "content-map-files", "content-map-mismatch", "content-map-missing", "content-map-unreadable", "content-native-extra", "content-native-files", "content-native-mismatch", "content-native-missing", "content-native-unreadable", "content-room-extra", "content-room-files", "content-room-mismatch", "content-room-missing", "content-room-unreadable" },
        ["core-gear-admission-{owner}"] = new HashSet<string>(StringComparer.Ordinal) { "core-gear-admission-NAinfini.ForgeEngine.Weapon", "core-gear-admission-NAinfini.ForgeWeaponEnergyLab" },
        ["core-gear-offer-projection-{owner}"] = new HashSet<string>(StringComparer.Ordinal) { "core-gear-offer-projection-NAinfini.ForgeEngine.Weapon", "core-gear-offer-projection-NAinfini.ForgeWeaponEnergyLab" },
        ["core-gear-restriction-left-slot-empty-{slot}"] = new HashSet<string>(StringComparer.Ordinal) { "core-gear-restriction-left-slot-empty-GearClass", "core-gear-restriction-left-slot-empty-GearMelee", "core-gear-restriction-left-slot-empty-GearSpecial", "core-gear-restriction-left-slot-empty-GearStandard" },
        ["level_cleanup_{phase}"] = new HashSet<string>(StringComparer.Ordinal) { "level_cleanup_after", "level_cleanup_before" },
        ["native-bot.{code}"] = new HashSet<string>(StringComparer.Ordinal) { "native-bot.already-submitted", "native-bot.ammo-storage-missing", "native-bot.applied-readback-changed", "native-bot.backpack-missing", "native-bot.bot-replaced", "native-bot.character-agent-already-spawned", "native-bot.character-assignment-refused", "native-bot.character-changed", "native-bot.character-invalid", "native-bot.character-occupied", "native-bot.character-readback-mismatch", "native-bot.character-restore-mismatch", "native-bot.character-restore-refused", "native-bot.gear-ambiguous", "native-bot.gear-missing", "native-bot.gear-slot-mismatch", "native-bot.host-required", "native-bot.human-slot", "native-bot.loadout-invalid", "native-bot.lobby-required", "native-bot.permission-readback-mismatch", "native-bot.request-invalid", "native-bot.session-changed", "native-bot.single-player-expedition", "native-bot.slot-invalid", "native-bot.slot-not-ready" },
        ["recorder.renew-close-{closed}"] = new HashSet<string>(StringComparer.Ordinal) { "recorder.renew-close-producerthread", "recorder.renew-close-timedout", "recorder.renew-close-writerthread" },
        ["room-{roomRefusal}"] = new HashSet<string>(StringComparer.Ordinal) { "room-multiple-rooms", "room-no-room", "room-no-world", "room-prefab-not-loaded", "room-zone-unresolved" },
        ["{prefix}-level-owner"] = new HashSet<string>(StringComparer.Ordinal) { "dimension-landing-level-owner", "fixed-bulkhead-key-level-owner", "fixed-controller-level-owner", "fixed-enemy-level-owner", "fixed-item-level-owner", "fixed-placement-level-owner", "fixed-sack-level-owner", "spawn-rule-level-owner" },
        ["{prefix}-owner"] = new HashSet<string>(StringComparer.Ordinal) { "dimension-landing-owner", "fixed-bulkhead-key-owner", "fixed-controller-owner", "fixed-enemy-owner", "fixed-item-owner", "fixed-placement-owner", "fixed-sack-owner", "spawn-rule-owner" },
        ["{value}-recorder-backpressure"] = new HashSet<string>(StringComparer.Ordinal) { "enemy-appearance-recorder-backpressure", "enemy-limbs-recorder-backpressure" },
    };
}
