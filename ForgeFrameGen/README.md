# ForgeFrameGen

Adds DLSS / FSR upscaling and frame generation to GTFO: NVIDIA® DLSS Super Resolution on NVIDIA RTX cards, AMD FSR™ upscaling on every other DirectX 12 card, NVIDIA® DLSS Frame Generation on RTX 40 and 50 series, and AMD FSR™ Frame Generation on every other DirectX 12 card. Upscaling and frame generation are picked separately, in any pairing.

## Requirements

- Upscaling: any graphics card that runs DirectX 12. `Auto` picks NVIDIA DLSS on an NVIDIA RTX card, AMD FSR 4 on the AMD Radeon RX 7000 and 9000 series, and AMD FSR 3.1 on every other card. DLSS is supported on RTX 40 and 50 series and untested on RTX 20 and 30 series; FSR 4 has not yet been tried on an AMD card.
- Frame Generation: windowed or borderless mode and an upscaling mode other than `Off` (DLAA counts). NVIDIA DLSS Frame Generation (with Reflex) needs a GeForce RTX 40 or 50 series card with a recent driver and Windows' hardware-accelerated GPU scheduling turned on; every other DirectX 12 card gets AMD FSR Frame Generation, which has no Reflex. You can pick either one on an RTX 40 or 50 card; a switch takes a fraction of a second while you play. If DLSS Frame Generation could not start when the game started (for example with hardware-accelerated GPU scheduling off), turning that on needs a restart of the game.

## Settings

At the end of **Video > Advanced Graphics** in the game. A change applies at once; hold the cursor on a setting for what it does. The settings are the upscaler (Auto, DLSS, FSR; a pick this card can't run falls back to Auto and the setting's hint says so), the upscaling mode (DLAA / Native AA, Quality, Balanced, Performance, Ultra Performance, Off), DLSS model, exposure, Frame Generation (Off, Auto, DLSS, FSR; a pick this PC can't run falls back to the other one and the setting's hint says so), HUD in generated frames, Reflex and the frame limit.

## The extra step

With this mod installed, every finished frame is handed to your screen through Direct3D 12. That is what lets you switch DLSS and Frame Generation on and off in the game without a restart. It costs a little on every frame, even with Frame Generation off; we have not measured how much. Uninstall the mod to get rid of it.

## Known limits

- The first-person weapon and particles have no motion vectors, so they can smear while they move fast.
- DLSS works after the game's own post-processing, so film grain and similar effects are upscaled with it.

## NVIDIA® DLSS

The package ships NVIDIA's DLSS libraries (`nvngx_dlss.dll`, `nvngx_dlssg.dll`) unchanged under the NVIDIA RTX SDKs License (`NVIDIA-RTX-SDKs-LICENSE.txt`), and NVIDIA's Streamline libraries (`sl.*.dll`) under `NVIDIA-Streamline-LICENSE.txt`; `sl.reflex.dll` carries NVIDIA® Reflex under `NVIDIA-Reflex-LICENSE.txt`. The mod loads only the copies in its own folder. NVIDIA, RTX, DLSS and Reflex are trademarks of NVIDIA Corporation. See `NOTICE`.

## AMD FSR™

The package ships AMD's FidelityFX SDK libraries (`amd_fidelityfx_loader_dx12.dll`, `amd_fidelityfx_upscaler_dx12.dll`, `amd_fidelityfx_framegeneration_dx12.dll`) unchanged under the MIT terms of the SDK's licence (`AMD-FSR-SDK-LICENSE.txt`). FSR works through Direct3D 12, so with the game on Direct3D 11 every upscaled frame takes a few extra copies between the two. AMD, Radeon, FidelityFX and FSR are trademarks of Advanced Micro Devices, Inc. This mod is not an NVIDIA or AMD product. See `NOTICE`.