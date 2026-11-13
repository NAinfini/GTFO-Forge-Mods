# ForgeEmote for developers

This mod holds content and rules only. Clip playback, the first-person arms and showing your own body are ForgeVisual; the per-player state, motion readout, orbit camera and game communication lines are ForgeCore; the wheel, key binding and timing (this mod only registers `H`, `Hold`) and player marker are ForgeUI; sounds are ForgeAudio. Anything missing belongs in those libraries, not here. `Tools/Forge/validate-dependencies.ts` fails if this package's source contains `HarmonyPatch`, `Harmony.Patch`, `FPSCamera`, `CameraManager`, `Animator` or `NetworkAPI`.

## Build

```
dotnet build Mods/ForgeEmote/ForgeEmote.csproj -c Release --artifacts-path artifacts/dotnet -p:GTFOBepInExPath=<profile>/BepInEx
```

Clips and icons are local media under `Mods/ForgeEmote/Assets`. The build places them beside the definitions in the output's `ForgeEmote` folder. Each clip `<id>.glb` (a Forge animation file, read by the Visual part of ForgeCore with `JointClip.Read`) made by the tool below ships as it is. Clips are made from `Assets/Clips/sources.json`:

```
pnpm ts Tools/Models/build-joint-clips.ts --sources Mods/ForgeEmote/Assets/Clips/sources.json --blender <blender.exe>
```

How to read that tool's previews is in `Mods/ForgeCore/DEVELOPERS.md` (Visual), "Binding a skeleton".

Sounds are local media too: `Assets/Audio/*.ogg` beside `Assets/Audio/cues.forge` (in Git with `Assets/Audio/LICENSE.forge`), converted with ForgeAudio's `tools/convert_audio.py convert <sources> Mods/ForgeEmote/Assets/Audio` and checked with its `validate`; the build puts them in the output's `ForgeEmote/audio`.

## Adding actions

Every folder called `ForgeEmote` under the plugins tree is scanned for `*.emote.forge`; this mod's own twenty-two actions load the same way. Or call `ForgeEmote.Api.Emotes.Register(definition, out error)` from your plugin's `Load`. Registration closes on first entering a lobby. `EmoteDefinition` (`ForgeEmote.Api`) has one property per field of the table below, with the same defaults except that a code definition sets what the file would derive from `Page` itself (`Body`, `View`, `Move`, `Loop`, `HideHeld`, `CancelOnHit`); `Folder` is the directory that `Clip`, `FirstPersonClip`, `Sound`, `lang/` and `audio/` are read from (a definition file sets it to its own folder; a code definition must set it). `Register` applies the same checks as a file (`EmoteRules.Validate`): an `Id` without `pack:entry`, an empty `Name`, `Page` or `Clip`, a `Slot` outside 0 to 7, a `Body`, `View` or `Move` outside the list, a `FadeIn`, `FadeOut` or `PairHold` out of range, an `Id` already taken, a missing clip file; it returns false with the reason in `error` and nothing is added. Players compare their actions one by one (ForgeCore `NetChannel.Manifest`, each action's wire number and clip hash): an action both have with the same clips plays on both sides; one a player lacks, or whose clips differ, is not shown to the other (never a wrong one in its place) and each side's log names those actions. Everything else plays as usual, and the game itself carries on.

| Field | Meaning | Default |
| --- | --- | --- |
| `Id` | `pack:entry`, never changed once published. Its FNV-1a hash is the wire number; a clash is refused. | required |
| `Name` | Language key in `lang/<language>.forge` beside the file. | required |
| `Icon` | ForgeUI icon name, `pack:entry`; register it with `UIKitMod.Icons`. | none |
| `Page` | `tactical`, `gesture`, `dance` or your own, titled by the language key `page.<id>`. | required |
| `Slot` | Place on the page, 0 to 7. | 0 |
| `Clip` | Clip file in the folder, played on the body. | required |
| `FirstPersonClip` | Clip made for the first-person view. Without it no arms are shown. | none |
| `Body` | `left-arm`, `right-arm`, `arms`, `upper`, `full`. What other players see, and the local body in third person; seen from the eye the local body moves only its arms. | tactical `left-arm`, gesture `upper`, dance `full` |
| `Pair` | A two-player action: the player's hand goes out and stays out (the clip waits at `PairHold`) for up to 15 s while a teammate within 1.5 m (same dimension) answers with the game's interaction prompt (use key) or by picking the same action on their wheel (their slot glows). The answerer steps to 1.2 m in front of the offerer where the level allows, both face each other and play the clip together; either one cancelling (moving, any action key) or going away ends it for both. Rides on the ordinary action state (action, sequence, phase, partner). | `false` |
| `PairHold` | For a `Pair` action: seconds into the clip where the hands are out; the clip waits there and the clasp is held there for 1.5 s once both are in. Must be less than the clip's length (refused otherwise). | `0` |
| `Loop` | Repeats until cancelled. | dance only |
| `FadeIn`, `FadeOut` | Seconds. | 0.15, 0.2 |
| `View` | `first-person`, `third-person`. | dance third, others first |
| `Move` | `free`, `locked`. | tactical free, others locked |
| `HideHeld` | Hides the held item. | gesture, dance |
| `CancelOnHit` | Damage ends it. | dance |
| `Crude` | A viewer can choose not to see it. | false |
| `Comms` | Text ID of the game's communication line said at the start; 0 for none. | 0 |
| `Ping` | Places the player marker at the crosshair. | false |
| `Sound` | ForgeAudio cue in the `audio` folder beside the file (its `cues.forge` and `.ogg` files, ForgeAudio's own format; one library per folder). Heard from the body, started where the action is, so every screen hears the same moment: a loop (for a looping action) joins where it is, a one-shot only within 0.5 s of the start, so a late joiner sees the rest without it (a `Pair` action counts its start from when the hand goes out, not from `PairHold` into the clip). Ends with the action; keep a one-shot no longer than its clip. | none |

Clip format and how to make one: `Docs/forge-emote/PLAN.md` §5 and `Mods/ForgeCore/DEVELOPERS.md` (Visual).

## The player's wheel file

`BepInEx/config/ForgeEmote/wheel.forge` holds one object: a page name to the action IDs on that page, in the order they appear. `tactical`, `gesture` and `dance` are the built-in pages; another page's own name may be used. A page the file names shows only the actions it lists, in that order, up to eight; a page it does not name keeps its definitions' own order, and an action it names that is not installed is reported and left out. The three built-in pages come first, then any other page in the order first seen.

```json
{ "gesture": ["ForgeEmote:wave", "ForgeEmote:cheer"] }
```

## Checking and packaging

`python Mods/verify.py ForgeEmote:ForgeEmoteCore` runs the definition-file contract test (a valid file reads; an unknown field, a Body outside the list and an Id without a colon are refused with the path of the field; a code definition with the same faults is refused by the same `EmoteRules.Validate`). `python Mods/ForgeEmote/tools/package_release.py --artifacts-path <dotnet artifacts> --output <folder>` writes the Thunderstore zip from the Release build, which carries the shipped definition file, its lang folder, the clips and the icons; the clips and icons are local media. It does not publish.

`ForgeEmote.EmoteControl` (static, main thread) is the wheel without the mouse, for another mod or ForgeDevelopment's `drive_cli.py call static:ForgeEmote.EmoteControl <Method> [JSON]`: `List`, `Play [id]` (the wheel's own `EmoteRunner.Start`), `Stop`, `State`, `Favourite id on|off`, `Favourites`, `Highlight`. Each returns one line, `refused: <why>` when it cannot.
