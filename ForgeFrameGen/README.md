# ForgeFrameGen

Adds DLSS / FSR upscaling and frame generation to GTFO: NVIDIA® DLSS Super Resolution on NVIDIA RTX cards, AMD FSR™ upscaling on every other DirectX 12 card, NVIDIA® DLSS Frame Generation on RTX 40 and 50 series, and AMD FSR™ Frame Generation on every other DirectX 12 card. Upscaling and frame generation are picked separately, in any pairing.

Settings are at the end of **Video > Advanced Graphics** in the game and apply at once. Needs a card that runs DirectX 12; Frame Generation needs windowed or borderless mode. With the mod installed every frame is presented through Direct3D 12, which costs a little even with everything off; uninstall the mod to get rid of it.

## NVIDIA® DLSS

The package ships NVIDIA's DLSS libraries (`nvngx_dlss.dll`, `nvngx_dlssg.dll`) unchanged under the NVIDIA RTX SDKs License (`NVIDIA-RTX-SDKs-LICENSE.txt`), and NVIDIA's Streamline libraries (`sl.*.dll`) under `NVIDIA-Streamline-LICENSE.txt`; `sl.reflex.dll` carries NVIDIA® Reflex under `NVIDIA-Reflex-LICENSE.txt`. The mod loads only the copies in its own folder. NVIDIA, RTX, DLSS and Reflex are trademarks of NVIDIA Corporation. See `NOTICE`.

## AMD FSR™

The package ships AMD's FidelityFX SDK libraries (`amd_fidelityfx_loader_dx12.dll`, `amd_fidelityfx_upscaler_dx12.dll`, `amd_fidelityfx_framegeneration_dx12.dll`) unchanged under the MIT terms of the SDK's licence (`AMD-FSR-SDK-LICENSE.txt`). AMD, Radeon, FidelityFX and FSR are trademarks of Advanced Micro Devices, Inc. This mod is not an NVIDIA or AMD product. See `NOTICE`.