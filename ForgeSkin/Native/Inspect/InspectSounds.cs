using ForgeAudio;
using ForgeSkin.Format;
using GameData;
using Gear;
using UnityEngine;

namespace ForgeSkin.Plugin;

// The sounds of a weapon inspect (InspectMotion.Cues, the table's "sounds"): each cue plays its roles, each role one of the game's own
// sound events for the gun in hand. A role is found in the gun's own reload and fire sequences (ReloadSounds: the sound items of its
// front part's reload sequence, else its stock part's, then both parts' fire sequences, by trigger time, names trimmed and lower case),
// else by its kind's default, else (sightIn, sightOut) as the gun's own aim sound. The local player hears the event's 2D form in their
// head; a teammate's gun plays its "_3rd" form, following the gun; an event with no "_3rd" form is 3D only and follows the gun for
// both. Nothing goes over the network: each machine plays the motion every player is seen playing (Inspect's per-player state). A role
// that finds no event is logged once per gun, motion and role and left out.
static class InspectSounds
{
    // One sound of a look: when (seconds into it) and the event this machine posts, in the player's head or on the gun.
    public readonly record struct Sound(float Time, uint Event, bool OnGun);

    static readonly Dictionary<uint, string[]> Sequences = new();
    static readonly Dictionary<string, uint> Ids = new(StringComparer.OrdinalIgnoreCase); // 0: the game has no such event (logged once by ForgeCore)
    static readonly HashSet<string> Told = new(StringComparer.Ordinal);

    // The sounds of `look`'s cues for the gun `item` (offline gear `gear`, of `kind`), as the local player (`self`) or a teammate hears them.
    public static Sound[] For(InspectTable table, InspectMotion motion, float slow, ItemEquippable item, uint gear, string kind, bool self)
    {
        var list = new List<Sound>();
        foreach (var (time, cue) in motion.Cues(slow))
        {
            if (!table.CueRoles.TryGetValue(cue, out var roles))
            {
                if (Told.Add("cue/" + cue)) ForgeSkinPlugin.Note($"inspect {motion.Id}: cue {cue} is not in the table's sounds; it plays nothing");
                continue;
            }
            foreach (var (role, after) in roles)
            {
                var name = Event(table, role, item, gear, kind);
                if (name == null)
                {
                    if (Told.Add($"{gear}/{motion.Id}/{role}")) ForgeSkinPlugin.Note($"inspect: gear {gear} ({kind}) has no sound for role {role} of {motion.Id}; that beat plays none");
                    continue;
                }
                var third = Id(name + "_3rd", quiet: true);
                if (third == 0) { if (Id(name) is var only and not 0) list.Add(new Sound(time + after, only, true)); }
                else if (self) { if (Id(name) is var flat and not 0) list.Add(new Sound(time + after, flat, false)); }
                else list.Add(new Sound(time + after, third, true));
            }
        }
        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list.ToArray();
    }

    public static void Play(in Sound sound, GameObject? gun)
    {
        if (!sound.OnGun) AudioKit.PlayGameEvent(sound.Event);
        else if (gun != null && !gun.WasCollected) AudioKit.PlayGameEvent(sound.Event, gun);
    }

    // The event a role plays for this gun, or null.
    static string? Event(InspectTable table, string role, ItemEquippable item, uint gear, string kind)
    {
        if (!table.Roles.TryGetValue(role, out var rule)) return null;
        if (rule.Zoom != 0) return Zoom(item, rule.Zoom > 0);
        foreach (var pattern in rule.Match)
            foreach (var name in ReloadSounds(gear))
                if (Matches(name, pattern)) return name;
        return rule.Default.TryGetValue(kind, out var own) ? own : rule.Default.TryGetValue("*", out var any) ? any : null;
    }

    static bool Matches(string name, string pattern)
    {
        bool head = pattern.StartsWith('*'), tail = pattern.EndsWith('*');
        var core = pattern.Trim('*');
        return head && tail ? name.Contains(core, StringComparison.Ordinal) : head ? name.EndsWith(core, StringComparison.Ordinal) : tail ? name.StartsWith(core, StringComparison.Ordinal) : name == core;
    }

    // The gun's own aim sound (in or out), or null.
    static string? Zoom(ItemEquippable item, bool zoomIn)
    {
        var audio = item.AudioData;
        if (audio == null) return null;
        var name = zoomIn ? audio.eventZoomIn : audio.eventZoomOut;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    // The sound events of the gun's own reload and fire sequences, read once per gear.
    static string[] ReloadSounds(uint gear)
    {
        if (gear == 0) return Array.Empty<string>();
        if (Sequences.TryGetValue(gear, out var known)) return known;
        var names = new List<string>();
        try
        {
            var block = GameDataBlockBase<PlayerOfflineGearDataBlock>.GetBlock(gear);
            if (block != null && !string.IsNullOrEmpty(block.GearJSON))
            {
                var range = new GearIDRange(block.GearJSON);
                var front = GameDataBlockBase<GearFrontPartDataBlock>.GetBlock(range.GetCompID(eGearComponent.FrontPart));
                var stock = GameDataBlockBase<GearStockPartDataBlock>.GetBlock(range.GetCompID(eGearComponent.StockPart));
                void Take(Il2CppSystem.Collections.Generic.List<WeaponAnimSequenceItem>? sequence)
                {
                    if (sequence == null) return;
                    var items = new List<(float Time, string Name)>();
                    foreach (var entry in sequence)
                        if (entry != null && entry.Type == eWeaponAnimSequenceItemType.Sound && !string.IsNullOrWhiteSpace(entry.StringData)) items.Add((entry.TriggerTime, entry.StringData.Trim().ToLowerInvariant()));
                    names.AddRange(items.OrderBy(i => i.Time).Select(i => i.Name));
                }
                Take(front != null && front.ReloadSequence != null && front.ReloadSequence.Count > 0 ? front.ReloadSequence : stock?.ReloadSequence);
                Take(front?.FireSequence);
                Take(stock?.FireSequence);
            }
        }
        catch (Exception e) { ForgeSkinPlugin.Note($"inspect: the reload sounds of gear {gear} could not be read ({e.Message}); its beats play the defaults"); }
        return Sequences[gear] = names.ToArray();
    }

    // An event's ID, looked up once; 0 when the game has none (logged once by ForgeCore; a try for a "_3rd" form says nothing).
    static uint Id(string name, bool quiet = false)
    {
        if (Ids.TryGetValue(name, out var id)) return id;
        var found = quiet ? AudioKit.HasGameEvent(name, out var tried) ? tried : 0u : AudioKit.TryGameEventId(name, out var named) ? named : 0u;
        return Ids[name] = found;
    }
}
