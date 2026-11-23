# ForgeFrameGen developer notes

The plan is `Docs/forge-upscale/PLAN.md`. ForgeFrameGen is one BepInEx plugin (`ForgeFrameGen.csproj`), a native bridge (`native/`, C++, built into `ForgeFrameGen.Native.dll`) and a preloader patcher (`preloader/`). The latest validation run is in `VALIDATION.md`. Players should read `README.md`.

## Build and package

Run from the repository root. Replace `<profile>` with a GTFO r2modman profile.

```
pwsh Mods/ForgeFrameGen/tools/build_native.ps1
dotnet build Mods/ForgeFrameGen/ForgeFrameGen.csproj -c Release --artifacts-path <dotnet artifacts> -p:GTFOBepInExPath=<profile>/BepInEx
python Mods/ForgeFrameGen/tools/package_release.py --artifacts-path <dotnet artifacts> --output <folder>
```

The C# build runs `tools/build_native.ps1` itself when the native sources or the SDK pins are newer than the built DLL. The script fetches the vendors' pinned files (`dlss-sdk.json`, `streamline-sdk.json`, `fidelityfx-sdk.json`) and checks each against its SHA-256. `package_release.py` ships only vendor files equal to their pin. `tools/accept_frame_generation.ps1` is the in-game acceptance run for frame generation on a development drive session.

## Requirements by feature

- Upscaling: any graphics card that runs DirectX 12. `Auto` picks NVIDIA DLSS on an NVIDIA RTX card, AMD FSR 4 on the AMD Radeon RX 7000 and 9000 series, and AMD FSR 3.1 on every other card. DLSS is supported on RTX 40 and 50 series and untested on RTX 20 and 30 series; FSR 4 has not yet been tried on an AMD card.
- Frame Generation: windowed or borderless mode and an upscaler other than `Off` (any mode, DLAA included). NVIDIA DLSS Frame Generation (with Reflex) needs a GeForce RTX 40 or 50 series card with a recent driver and Windows' hardware-accelerated GPU scheduling turned on; every other DirectX 12 card gets AMD FSR Frame Generation, which has no Reflex. You can pick either one on an RTX 40 or 50 card; a switch takes a fraction of a second while you play. If DLSS Frame Generation could not start when the game started (for example with hardware-accelerated GPU scheduling off), turning that on needs a restart of the game.

## Settings

At the end of **Video > Advanced Graphics** in the game. A change applies at once; hold the cursor on a setting for what it does. The settings are the upscaler (Auto, DLSS, FSR, Off; a pick this card can't run falls back to Auto and the setting's hint says so; Off turns upscaling and Frame Generation off, the game's own anti-aliasing runs and every other setting below is dimmed), the upscaling mode (DLAA / Native AA, Quality, Balanced, Performance, Ultra Performance), DLSS model, exposure, Frame Generation (Off, Auto, DLSS, FSR; a pick this PC can't run falls back to the other one and the setting's hint says so), HUD in generated frames, Reflex and the frame limit.

The game's own maximum queued render frames setting (最大渲染队列帧数 in Chinese) can stay at any value, 0 included; Frame Generation runs with all of them.

## The Direct3D 12 present step

With this mod installed, every finished frame is handed to the screen through Direct3D 12. That is what lets DLSS and Frame Generation switch on and off in the game without a restart. It costs a little on every frame, even with Frame Generation off; the cost has not been measured. Uninstalling the mod removes it. FSR works through Direct3D 12, so with the game on Direct3D 11 every upscaled frame takes a few extra copies between the two.

## Known limits

- The first-person weapon and particles have no motion vectors, so they can smear while they move fast.
- DLSS works after the game's own post-processing, so film grain and similar effects are upscaled with it.