# ForgeAudio for developers

This file is for mod authors. Players should read `README.md`.

Drop Ogg files and a `cues.json` in a folder next to your DLL, load it with one line, and play by cue name. 2D, positioned and following sounds, volume values, voice limits and wall occlusion are handled for you. Every mod using the kit shares one mix: global categories with one voice limit each, ducking across mods, and one master and per-category volume in the game's own audio settings. The kit also plays a sound for every player in the session and picks a volume and pitch per play from a range.

The kit needs only BepInExPack_GTFO and the game. Within 1.x, public members are only added, never removed or changed. Internal classes, log wording and packaging are not part of the public API.

## Layering: what this package holds and what it does not

ForgeAudio holds sound mechanisms: playing plain audio files by cue, 3D positioning, mix categories and global limits, occlusion, long-running sounds, plays every player hears, and replacing or muting vanilla sounds (player body sounds, weapon action sounds). It ships no sounds. Your audio files and `cues.json` stay in your mod; load them once and decide when to play and what to replace. A reusable mechanism goes here even before a second mod needs it.

## Folder layout

| Path | Contents |
|---|---|
| `Core/` | Plain logic with no Unity reference: the cue catalog, the mixer, budgets. |
| `Native/` | The plugin: decoding, playback, voices, occlusion, settings. |
| `vendor/NVorbis/` | The Ogg Vorbis decoder (MIT) and its notice. |
| `tools/` | `convert_audio.py` (convert, validate, render), `cue_render.py`, `package_release.py`. |
| `tests/` | Offline tests with real Ogg fixtures. |

## Build, test and package

Run from the repository root. Replace `<profile>` with a GTFO r2modman profile.

```
dotnet build Mods/ForgeAudio/ForgeAudio.csproj -c Release --artifacts-path artifacts/dotnet --disable-build-servers -m:1 -p:GTFOBepInExPath=<profile>/BepInEx
dotnet run --project Mods/ForgeAudio/tests/Core/ForgeAudioCore.csproj -c Release
python Mods/ForgeAudio/tools/test_tools.py
python Mods/ForgeAudio/tools/package_release.py --artifacts-path artifacts/dotnet
```

When a game profile is configured, the tests also check every installed `audio` folder. Without one those checks report "not run".

`package_release.py` checks the Thunderstore metadata, the BepInEx-only dependency, version equality and the 256 x 256 icon. It writes a deterministic zip with the DLL, NVorbis and its license, `manifest.json`, `README.md`, `DEVELOPERS.md`, `CHANGELOG.md`, `LICENSE` and `icon.png`. It does not publish.

## Quick start

**1. Put your sounds next to your DLL, with `cues.json`.**

```
BepInEx/plugins/MyMod/
  MyMod.dll
  audio/
    click.ogg
    explosion.ogg
    engine.ogg
    cues.json
```

```json
{
  "buses": {
    "menu":  { "category": "ui" },
    "blast": { "category": "explosion", "duck": { "categories": ["debris", "ambience"], "db": 6 } },
    "motor": { "category": "vehicle" }
  },
  "cues": {
    "click":     { "bus": "menu", "place": "2d" },
    "explosion": { "bus": "blast", "priority": 200 },
    "engine":    { "bus": "motor", "loop": true }
  }
}
```

Have WAV, MP3 or FLAC files? Convert a whole folder (needs Python 3.10+ and FFmpeg with libvorbis):

```
python convert_audio.py convert my-sounds/ BepInEx/plugins/MyMod/audio/
```

**2. Reference `ForgeAudio.dll`, declare the dependency and load once with your plugin ID.**

```xml
<Reference Include="ForgeAudio" HintPath="$(GTFOBepInExPath)/plugins/NAinfini-ForgeAudio/ForgeAudio.dll" Private="false" />
```

```csharp
using BepInEx;
using BepInEx.Unity.IL2CPP;
using ForgeAudio;
using UnityEngine;

[BepInPlugin(Id, "My Mod", "1.0.0")]
[BepInDependency(AudioKit.PluginId)]
public class Plugin : BasePlugin
{
    public const string Id = "author.mymod";
    internal static AudioLibrary Sounds = null!;

    public override void Load() => Sounds = AudioKit.Load(Id);
}
```

**3. Play, from any main-thread code.**

```csharp
Plugin.Sounds.Play("click");                                          // 2D, the local player hears it
Plugin.Sounds.Play("explosion", position);                            // 3D at a point
var engine = Plugin.Sounds.Play("engine", vehicle).Handle;            // 3D, follows a transform
engine?.Stop(0.5f);                                                   // fade out over half a second
```

Add `NAinfini-ForgeAudio-0.1.0` to your Thunderstore `dependencies`. Files decode in the background right after `Load`. Problems (a missing folder, a file that is not Ogg Vorbis, an unknown cue name) are written to the BepInEx log as `author.mymod/cue`.

## Joining the shared mix

1. Load with your plugin ID. Your cues are `Plugin.Id/cue` in every log line, so two mods can both have a `click`.
2. Group your cues into buses and map each bus to one of the global categories (`ui`, `weapon`, `explosion`, `debris`, `ambience`, `voice`, `vehicle`, `device`, `music`). Pick by what the sound is, not by which mod plays it.
3. Your own limits (`maxVoices`, `maxPerEmitter`, `minInterval`, bus `seats`) apply first. The category limit applies on top, over every mod's sounds of that category. When a category is full, the weakest sound of any mod is pushed out for a stronger or nearer newcomer. Otherwise the newcomer is refused. Give important sounds a higher `priority`.
4. Duck categories, not your own buses: `"duck": { "categories": ["debris"], "db": 6 }` lowers every mod's debris while your bus plays.
5. Do not add a volume setting to your mod. Players set the kit's master and category volumes in the game's audio settings (a mod that also uses ForgeUI adds the rows, see below).

### Volumes

The kit's volumes (the master and one per category) are `AudioVolumes`: `Master`, `Get(category)` / `Set(category, value)` (0 to 1, the cues.json category names), `ResetAll()` (everything to full, saved at once) and `Controls` (the master, then the nine categories: `VolumeControl` with `Id`, `English`, `Chinese`, `Get`, `Set`; `HeadingEnglish` / `HeadingChinese` name the group). A change applies to every mod's sounds the same frame and is saved to `BepInEx/config/NAinfini.ForgeAudio.cfg` (`Volume / Master` and one entry per category) half a second later. A category's volume is the game's master x the slider it follows (Music, Dialogue or SFX) x the kit's master x the category's value.

The kit draws no settings page (ForgeAudio does not depend on ForgeUI). A package that uses both libraries lists the volumes in the game's Settings, Audio page once, in its `Load`:

```csharp
ForgeUI.SettingsPages.AddAudioSliders("ForgeAudio.volumes", AudioVolumes.HeadingEnglish, AudioVolumes.HeadingChinese,
    AudioVolumes.Controls.Select(c => new ForgeUI.SettingsSlider(c.Id, c.English, c.Chinese, c.Get, c.Set)).ToList(), AudioVolumes.ResetAll);
```

The same section id is added only once however many packages call it. ForgeEngine, ForgeOrdnance (with Arsenal and Frames) and ForgeWeapon_EnergyLab do. If none of the installed packages does, the volumes stay in the config file.

## `audio/cues.json`

The file has two sections: `buses` (groups, each mapped to a global category) and `cues`. Every cue plays on a bus.

```json
{
  "buses": {
    "boom":   { "category": "explosion", "gainDb": -2, "seats": 3, "duck": { "categories": ["debris"], "db": 6 } },
    "debris": { "category": "debris", "seats": 2 },
    "ui":     { "category": "ui" }
  },
  "cues": {
    "big_boom": {
      "bus": "boom", "priority": 230, "maxSeconds": 1.8,
      "space": { "near": 8, "far": 180, "curve": [[0, 1], [40, 0.6], [180, 0]], "doppler": 0.5 },
      "layers": [
        { "clips": ["boom-1.ogg", "boom-2.ogg"], "distance": { "max": 40 }, "pitch": [0.97, 1.03] },
        { "clips": ["sub.ogg"], "offsetMs": 60, "gainDb": -4, "fadeOutMs": 300, "distance": { "max": 40 } },
        { "clips": ["rumble.ogg"], "distance": { "min": 40 }, "lowpassHz": 600 }
      ]
    },
    "click": { "bus": "ui", "place": "2d", "maxSeconds": 0.25 }
  }
}
```

A cue with no `layers` plays its own file `<cue>.ogg`. A file that a layer uses is not a cue of its own. A file no cue names is reported as unavailable. Comments and trailing commas are allowed. Files in subfolders are cues named `sub/name`. Cue names ignore case.

### Cue settings

| Setting | Default | Meaning |
|---|---|---|
| `layers` | the cue's own file | 1 to 8 layers, below |
| `gain` / `gainDb` | `1` / `0` | Cue volume: 0 to 4 linear, or -60 to 12 dB (not both). `[min, max]` picks a volume per play, evenly in decibels (from silence: linearly) |
| `pitch` | `1` | 0.1 to 3, or `[min, max]` for a random pitch per play |
| `loop` | `false` | Loops seamlessly until stopped. A loop has one layer with only `clips` and `gain` |
| `maxSeconds` | none | The longest the mix plays (0.05 to 60 s). It is cut there and fades out over 12% of the length (40 to 150 ms) |
| `bus` | required | The bus the cue plays on. Its category is the bus's |
| `priority` | `100` | 0 to 255. Higher takes a voice from lower, wins a bus seat and a place in a full category |
| `maxVoices` | `8` | How many of this cue may play at once (1 to 256) |
| `maxPerEmitter` | `maxVoices` | How many may play at once from one emitter |
| `minInterval` | `0` | Seconds. A play sooner than this after the last accepted one returns `Throttled` |
| `environment` | `true` | Wall and door muffling for placed sounds |
| `place` | `"auto"` | `"2d"` (in the listener's head), `"world"` (a fixed point), `"follow"` (follows the object and fades out in 150 ms if it is destroyed), `"auto"` (what the caller passed). Or `{ "self": ..., "other": ... }` for the local player's own sounds and everyone else's |
| `space` | below | Distance and spatialisation |

### Layers

Each layer is one sound inside the cue. `clips` is a pool of variants: a cue has one variant per clip of its largest pool, one is picked per play, and the other layers cycle through their own pools. A random pick never repeats the cue's previous variant (when it has two or more); `PlayOptions.Variant` plays as given. Per-play randomness lives in the cue: `pitch` and `gain` as `[min, max]`; a layer's own `pitch` range is fixed per variant.

| Layer setting | Default | Meaning |
|---|---|---|
| `clips` | required | 1 to 32 files in the folder |
| `offsetMs` | `0` | Start offset inside the cue |
| `gain` / `gainDb` | `1` / `0` | Layer volume |
| `range` | whole clip | `[startSeconds, endSeconds]` of the clip to use |
| `fadeInMs`, `fadeOutMs` | `0` | Fades of this layer |
| `pitch` | `1` | 0.1 to 3, or `[min, max]`, picked per variant |
| `distance` | everywhere | `{ "min": m, "max": m }`. The layer plays only while the listener is inside, so a far layer can replace the near one |
| `lowpassHz` | none | 50 to 20000. A low-pass on the layer, for a distant version made from the same clip |

Clips can have any sample rate or channel count. The mix uses the first layer's rate and is stereo when any clip is.

### Space

| `space` setting | Default | Meaning |
|---|---|---|
| `near`, `far` | `2`, `30` | Metres. Full volume within `near`, silent beyond `far` |
| `curve` | `"linear"` | `"linear"`, `"log"` or points `[[metres, gain], ...]` |
| `blend` | `1` | 0 hears the sound flat, 1 fully positioned |
| `spread` | `0` | 0 to 180 degrees. Widens the sound, which reduces panning |
| `doppler` | `0` | 0 to 5. Pitch shift from the relative motion of listener and sound |
| `cull` | `true` | Beyond `far` the sound is silent and takes no voice |

### Buses

| Bus setting | Default | Meaning |
|---|---|---|
| `category` | required | One of the nine categories |
| `gain` / `gainDb` | `1` / `0` | Scales every cue on the bus |
| `seats` | unlimited | How many of its sounds may play at once from one emitter. `1` makes the bus mutually exclusive. A newcomer takes a free seat, else fades out the weakest holder in 60 ms when it has a higher priority (or equal and not much quieter), else it is refused |
| `duck` | none | `{ "categories": [...], "db": 6 }`. Lowers every mod's sounds of those categories while any sound of this bus plays (40 ms down, 160 ms back). The deepest active duck applies |

### Categories

One limit per category, shared by all mods: ui 8, weapon 16, explosion 10, debris 12, ambience 8, voice 4, vehicle 48, device 10, music 2. At the limit, the weakest sound is faded out in 60 ms and ends with `PushedOut` when the newcomer outranks it (higher priority, or equal and nearer). Otherwise the newcomer is `BudgetRejected`. `music` follows the game's Music slider, `voice` the Dialogue slider, and every other category the SFX slider. The kit's master and category volumes multiply them.

### Errors

A misspelled or out-of-range cue setting makes only that cue unavailable, with the reason in the log. An unreadable or missing `cues.json`, an unknown top-level section, a bus without a known category, or a duck naming an unknown category fails the whole folder. A library holds at most 1024 cues and 1 MiB of settings.

## API

Namespace `ForgeAudio`. Call everything on Unity's main thread.

### `AudioKit`

| Member | |
|---|---|
| `const string PluginId` | `"NAinfini.ForgeAudio"`, for `[BepInDependency]` |
| `AudioLibrary Load(string identity, string? folder = null)` | Loads the cues `cues.json` lists in `folder` (by default the `audio` folder next to the calling DLL). `identity` is your plugin GUID and unique among libraries. Never throws. A library that cannot load has `Error` set and refuses every play |
| `int DimensionAt(Vector3 position, bool preferLocalPlayer = false)` | The dimension `Play` would use for a sound at `position`. Ask it for a point other than the sound's own, for example a pod's landing point while the pod is still in the sky, and pass the result as `PlayOptions.Dimension`. With `preferLocalPlayer` the local player's own dimension is asked first and stands in for a point off every mesh, even while the camera is elsewhere (a sound nearly always heard where the player is); with no local player the listener's dimension is used |
| `bool PostGameEvent(uint eventId, Vector3 position)` | Plays one of the game's own sound events at a world position, through the game's `CellSound` (the game's mix and sliders; not the kit's categories, limits or cues). False, logged, when the ID is 0, the call is off the main thread or the game refuses it. The game's grenade, mine and enemy explosion events (`FRAGGRENADEEXPLODE`, `STICKYMINEEXPLODE`, `EXPLODEREXPLODE`, `ENEMYEXPLODE`) start no voice when posted this way: play an explosion as a cue of your own |
| `bool PostGameEvent(string eventName, Vector3 position)` | The same, naming the event as `AK.EVENTS` does (case ignored), so your mod needs no reference to the game's Wwise or CellSound assemblies. The ID is looked up once. False, logged, for an unknown name |

### `AudioLibrary`

| Member | |
|---|---|
| `PlayResult Play(string cue, PlayOptions? options = null)` | 2D for the local listener |
| `PlayResult Play(string cue, Vector3 position, PlayOptions? options = null)` | 3D at a world position |
| `PlayResult Play(string cue, Transform follow, PlayOptions? options = null)` | 3D following a transform. If it is destroyed, the sound fades out over 150 ms and ends with `TargetDestroyed` |
| `PlayResult Play(string cue, GameObject follow, PlayOptions? options = null)` | The same on a game object. A destroyed object is refused as `TargetDestroyed` (logged quietly, an owner that is gone is an ordinary end), so check nothing before calling |
| `AudioVoice Voice(Transform anchor)` | A sound slot riding on `anchor`: at most one cue plays on it and a new play replaces the old. `Play(cue, gain = 1)` stops and starts (false when refused), `Hold(cue, gain)` keeps a loop going and only changes its level once it plays, `SetGain(gain)`, `Stop(fadeSeconds = 0)`, `Playing`, `IsPlaying(cue)`, `Anchor` |
| `void Require(params string[] cues)` | Throws `InvalidDataException` naming every cue the library lacks (or the load error); `InvalidOperationException` before loading finished. Call once after `IsLoaded`, before playing, so a missing cue stops the mod at startup |
| `bool Prewarm()` | Starts the next non-looping cue at zero volume so its first real play has no first-start hitch. Call once per frame; true when every cue was started or there is nothing to start, false while cues remain, the library is loading or no level camera has a listener yet. Other refusals of one cue 600 times in a row end it with one error line. The next level starts over |
| `AudioSustain Sustain(string cue, GameObject owner, PlayOptions? options = null, Func<bool>? keepWhile = null)` | The same on a game object; an owner that is already destroyed gives a sustain whose `IsActive` is false |
| `AudioSustain Sustain(string cue, Transform owner, PlayOptions? options = null, Func<bool>? keepWhile = null)` | A loop that stays on `owner`. It plays now, and plays again if the sound ended for any reason (level exit, checkpoint reload, device change, voice budget). It ends when the owner is destroyed or inactive, when `keepWhile` returns false, when the library is disposed, or on `Stop(fadeSeconds = 0.15)` |
| `void StopAll(float fadeSeconds = 0)` | Stops every sound of the library |
| `bool IsLoaded` | Every file has decoded or failed |
| `bool Has(string cue)` | The cue loaded and can play |
| `AudioFailure? Error` | Why the whole library cannot load |
| `AudioCue Cue(string cue)` | A reference to the cue that finds it once and plays it without looking the name up again; also plays it for every player. Make it once (before loading finished is fine) and keep it |
| `string Identity`, `string Folder`, `bool IsDisposed`, `void Dispose()` | |

### `PlayOptions`

Optional and read-only after creation. Reuse one instance for repeated plays; `PlayOptions.OfGain(gain)` returns a shared instance per gain for a mod's few fixed levels (main thread only).

| Property | Default | |
|---|---|---|
| `Gain` | `1` | Multiplies the cue gain, 0 to 4 |
| `Pitch` | random in the cue's range | Exact pitch, 0.1 to 3 |
| `Variant` | random | Zero-based variant number |
| `StartSeconds` | `0` | Start offset. Loops wrap it |
| `IsSelf` | `false` | The sound belongs to the local player, so the cue's `place.self` applies instead of `place.other` |
| `Dimension` | automatic | Game dimension of a world sound |
| `Emitter` | see right | Groups plays for `maxPerEmitter`. Default: one emitter per followed transform, one for all 2D plays, and each positioned play is its own |

### `PlayResult` and `AudioHandle`

| Member | |
|---|---|
| `PlayStatus Status` | `Played`, `Virtualized` (a loop admitted without a voice that starts when one frees), `DistanceCulled`, `BudgetRejected`, `Throttled`, `Failed` |
| `bool Accepted` | `Played` or `Virtualized` |
| `AudioHandle? Handle` | Present when accepted |
| `AudioFailure Error` | Why it failed or was rejected. Already logged; a repeated failure is written once per key |
| `handle.Stop(float fadeSeconds = 0)` | False when it had already ended |
| `handle.SetGain(gain)` | Applied on the next update. False when ended or out of range |
| `handle.State` | `Playing`, `Virtualized`, `Fading`, `Ended` |
| `handle.Ended` | Event, runs once on the main thread with the `EndReason`: `Completed`, `Stopped`, `Stolen`, `TargetDestroyed`, `LevelExit`, `CheckpointReload`, `LibraryDisposed`, `DeviceChanged`, `Failed`, `PluginUnloaded`, `Replaced`, `PushedOut` |

### `AudioCue`: a cue found once, and plays every player hears

| Member | |
|---|---|
| `Play(options)`, `Play(position, options)`, `Play(follow, options)` | As the library's `Play`, without a name lookup per play |
| `PlayResult PlayEverywhere(Vector3 position, PlayOptions? options = null, bool reliable = false)` | Plays here and for every other player in the session at the same point and dimension, with the same variant, pitch and volume (picked here). The result is this player's own play |
| `PlayResult PlayEverywhere(PlayOptions? options = null, bool reliable = false)` | The same in 2D: each player hears it in their own head |
| `bool IsReady`, `AudioLibrary Library`, `string Name` | |

How it travels: the kit sends one message through its own ForgeCore channel (`Net.Open("NAinfini.ForgeAudio")`); your mod registers nothing. ForgeCore's handshake refuses a player whose copy of the kit has another message layout, and the library and cue travel as hashes of their names, so every player needs the same mod with the same cue. Whoever calls it decides: call it on the host for a sound the host decides (an alarm, a boss phase), or on the player who caused it (a throw). Each player hears it as another player's sound (`IsSelf` is false on the others), the moment the message arrives. A player who joins later does not hear plays from before, so a loop is refused: start loops on each player yourself (`Sustain`). `reliable: true` uses the game's reliable channel, for rare sounds that must not be lost; the default is the unreliable one, for frequent presentation. A play refused here for the call itself (throttled, invalid options, the library not loaded) is not sent; one only this player cannot hear (too far, the category full, no listener yet) is.

```csharp
static AudioCue _siren = null!;
public override void Load()
{
    Sounds = AudioKit.Load(Id);
    _siren = Sounds.Cue("siren");          // found once, kept
}

// On the host, when the alarm starts: every player hears the same siren at the same point.
_siren.PlayEverywhere(panel.position, reliable: true);
```

### Replacing the game's body sounds

ForgeAudio is the one place a mod replaces the game's own sounds; do not patch the game's sound calls yourself. It covers a player's body (below) and weapon actions (further down). A player's body: the game plays every footstep, jump take-off, landing and end of a fall of a player through `FeedbackPlayer.Play`, and the kit's single hook on its three overloads asks the claim on that body, if any. Bodies nobody claimed play their sounds unchanged, and a lookup costs one pointer probe with no allocation.

| Member | |
|---|---|
| `[Flags] enum BodySound` | `Footstep` (every footstep: movement states and the third-person walk animation), `Movement` (jump take-off, landing, end of a fall) |
| `BodySoundClaim? AudioKit.ClaimBodySounds(string modId, GameObject root, BodySound kinds, IBodySoundHandler handler, int priority = 0)` | Takes over the `kinds` sounds of the body under `root` (usually a `PlayerAgent`'s GameObject) on this client. Every transform under `root` is collected now. Null, logged, for an invalid argument, off the main thread, or when the game's body sounds cannot be told apart by body |
| `bool IBodySoundHandler.Play(BodySound kind, Vector3 position)` | Called from inside the game's play. `position` is the footstep's own; for `Movement` it is `root`'s. Return true to drop the game's sound, false to let it play. If it throws, the game's sound plays and the error is logged once |
| `claim.Dispose()` | Gives the sounds back. Safe to call twice and after the level ended |
| `claim.Refresh()` | Collects `root`'s transforms again after parts were added or moved under it |
| `claim.Active`, `ModId`, `Root`, `Kinds`, `Priority` | `Active` is false once disposed, after level exit, or when the kit unloads |

Of several claims on one body only the highest `priority` is asked (the newer one at equal priority), and only for the kinds it claimed. Every claim ends at level exit. The kit sends nothing over the network: claim the body on every client that should hear the change.

```csharp
// While the suit is worn: the pilot's footsteps become the suit's, the jump and landing sounds are dropped.
sealed class Suit : IBodySoundHandler
{
    BodySoundClaim? _claim;
    public void Board(PlayerAgent pilot) => _claim = AudioKit.ClaimBodySounds(MyPlugin.Id, pilot.gameObject, BodySound.Footstep | BodySound.Movement, this);
    public void Leave() { _claim?.Dispose(); _claim = null; }
    public bool Play(BodySound kind, Vector3 position)
    {
        if (kind == BodySound.Footstep) Sounds.Play("exo.footstep", position);
        return true;
    }
}
```

### Weapon actions

The kit also raises three actions of a weapon you claim: the start and the end of a reload and a trigger pull on an empty magazine. The game's own weapon sounds come from the weapon's animation and stay; your handler adds the mod's cue. Weapons nobody claimed cost nothing (each hook reads one counter), a claimed weapon costs one pointer probe per action with no allocation.

| Member | |
|---|---|
| `[Flags] enum WeaponAction` | `ReloadStart` (the reload sequence really started), `ReloadDone` (the reload animation finished), `DryFire` (a trigger pull on an empty magazine; automatic, semi-automatic and burst alike) |
| `WeaponActionClaim? AudioKit.ClaimWeaponActions(string modId, Item weapon, WeaponAction kinds, IWeaponActionHandler handler, int priority = 0)` | Hears the `kinds` actions of this weapon instance on this client. Null, logged, for an invalid argument or off the main thread. Claim again when the game builds a new instance |
| `void IWeaponActionHandler.Play(WeaponAction action)` | Called from inside the game's event, once per action. If it throws, the error is logged once |
| `claim.Dispose()` | Lets go of the weapon. Safe to call twice and after the level ended |
| `claim.Active`, `ModId`, `Weapon`, `Kinds`, `Priority` | `Active` is false once disposed, after level exit, or when the kit unloads |

Of several claims on one weapon only the highest `priority` is asked (the newer one at equal priority), and only for the kinds it claimed. Every claim ends at level exit.

```csharp
// The suit gun settled in the slot: its reloads and empty clicks play the suit's cues.
sealed class SuitGunSound : IWeaponActionHandler
{
    WeaponActionClaim? _claim;
    public void Settled(Item gun) { _claim?.Dispose(); _claim = AudioKit.ClaimWeaponActions(MyPlugin.Id, gun, WeaponAction.ReloadStart | WeaponAction.DryFire, this); }
    public void Play(WeaponAction action) => Sounds.Play(action == WeaponAction.DryFire ? "suit.dry" : "suit.reload");
}
```

## How it behaves

- **The kit plays local sounds unless asked.** It does not decide when or for whom a sound plays. `Play` plays on this client only: your mod triggers it on every client that should hear it and passes `IsSelf` for the local player's own sounds. `AudioCue.PlayEverywhere` is the one call that sends: a one-shot every player hears, without a network message of your own.
- **Lifecycle.** Sounds end at level exit and checkpoint reload. Start the loops you still want again afterwards, or use `Sustain`. Saving a checkpoint does not stop sounds. When the output device changes, one-shots end with `DeviceChanged` and loops resume.
- **Loading.** `Load` returns at once. Decoding runs on worker threads. Playing a cue before its files finish returns `Failed` with `NotReady`. Decoded audio is cached (at most 256 MiB compressed, 512 MiB decoded) and identical files are shared between libraries. Each file is limited to 64 MiB compressed and one hour. Ogg Vorbis only: Opus and chained or multiplexed streams are refused.
- **Mixing.** A cue with more than one plain layer (or a `maxSeconds` cap) is mixed once after its files decode, so playing it costs one voice. The mixer's rules: the output rate is the first layer's; other rates are resampled; each layer is trimmed, pitched, low-passed, scaled, faded and summed at its offset; the sum is clamped. A random pitch comes from a hash of the cue, variant and layer, so the same variant always sounds the same.
- **Distance stretches.** The `distance` ranges of the layers split the listener distance into stretches. `Play` picks the stretch from the distance at the start of the play. A stretch where no layer plays returns `DistanceCulled`.
- **Voices.** There are 64 real voices and 256 logical states. Speech, warnings (`priority` 200 or more) and ambience loops each reserve eight voices. Higher priority steals from lower. Loops that lose their voice keep their place in the loop and resume.
- **Occlusion.** Walls and closed doors between the camera and the sound lower its volume, down to 0.35. At most four rays are cast per frame, and no voice is tested more often than every 0.2 s. There is no reverb and no low-pass at play time; make a distant version with a layer that has a `distance` range and `lowpassHz`.
- **Dimensions.** A world sound's dimension is the one whose navigation mesh contains the point, or the listener's own for points off every mesh. A following sound without an explicit `Dimension` is placed again when its object jumps more than 10 m at once (a teleport or a dimension warp). The listener is in the local player's dimension while the kit hears through that player's own camera; while spectating it takes the dimension of the player the camera is at.
- **Listener.** The kit reuses any enabled Unity listener. Otherwise it adds its own to the active look camera. It never moves or removes a listener it did not create. After one full pass in the menus the kit stops looking up the camera, the local player and the listener every frame; entering a level (a game state change) wakes it.

## Author tools

`tools/convert_audio.py` needs Python 3.10+ and FFmpeg with `libvorbis`. `validate` also needs the .NET SDK. The runtime never starts external tools.

```
python Mods/ForgeAudio/tools/convert_audio.py convert my-sounds/ path/to/audio/        # whole folder, keeps the names
python Mods/ForgeAudio/tools/convert_audio.py convert shot.wav path/to/audio/shot.ogg  # one file
python Mods/ForgeAudio/tools/convert_audio.py validate path/to/audio                   # exactly what Load will do
python Mods/ForgeAudio/tools/convert_audio.py render path/to/audio big_boom --out big_boom_far.wav --distance 90 [--variant 1]
```

- `convert` turns every WAV, MP3, FLAC and OGG into `<name>.ogg`, keeps subfolders, skips outputs that already exist, and reports each file that failed. It never normalizes or truncates.
- `validate` runs the runtime's own catalog code and decodes every clip, then prints a JSON summary. It exits non-zero if any cue is unavailable.
- `render` writes the mixed preview of a cue exactly as the game plays it at a listener distance (default 0 m). It exits 3 when the cue is silent at that distance.
- `tools/cue_render.py` prepares a source segment for a clip: onset alignment, short fades and loudness. Its `exact` rule keeps a segment you chose by ear as cut: no trimming or fades, and a quiet one-shot is lifted toward -14 LUFS (a layer of a layered cue toward -17; at most 12 dB, peaks held at -1 dBFS by a limiter, never lowered; a loop is not lifted). A source's `targetLufs` sets its own target and `false` keeps its level. A clip can never be louder than its file, because the kit scales a clip by the sliders and clamps at full volume; stage files loud enough instead of raising cue `gain` above 1.

Ship recordings you own or have the right to use, as real Ogg Vorbis. Rights remain the author's.

## Logging

Logging goes through ForgeCore's `ModLog` and only real failures are written: every `AudioLog.Report` failure (except a voice restarted after a device change) goes through one dedup layer, `AudioLog.Report(key, ...)` into `ErrorOnce`, so one key is written once. Periodic reports have fixed keys (`clip-destroyed`, `voice-destroyed`, `one-shot-stalled`, `emitter-non-finite`, `driver-update`). There are no information lines, timing, play counters or a Diagnostics setting.
