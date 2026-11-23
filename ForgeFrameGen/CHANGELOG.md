# Changelog

## 1.0.1

Needs Forge Core 1.0.4.

- Off moved from the upscaling mode to the upscaler setting: Upscaler Off turns upscaling and Frame Generation off and dims every other row. A config saved with Mode = Off is read as Upscaler = Off once.
- When the frame generation bridge can't go on (a graphics call fails or stalls, the graphics driver resets), or you pick exclusive fullscreen, the game now switches back to its own rendering and keeps running, without frame generation until it restarts; the log says why once. Before, the game could stop showing frames.
- On graphics cards that are not NVIDIA's, NVIDIA's frame generation library is no longer started at all.
- If no frame reaches the screen for ten seconds, the BepInEx log says where the bridge was, so a frozen game can be traced.
- Fixed: the picture could freeze when the game's maximum queued render frames setting (最大渲染队列帧数) was 0 or 1. Every value now works.
- Logs: the ten-second no-frame warning is only given once the first frame has reached the screen, so it no longer shows up while the game is still loading.
- When Auto can't use DLSS (upscaling or Frame Generation) on a PC with an NVIDIA card and takes AMD FSR, the log says so once with the reason and the card, and the setting's tooltip names the reason: driver too old, card not supported, hardware-accelerated GPU scheduling off, or the game running on another graphics card than the NVIDIA one.

## 1.0.0

- NVIDIA DLSS Super Resolution in place of the game's own anti-aliasing: DLAA at your screen's resolution, or Quality, Balanced, Performance and Ultra Performance, which render the world smaller and bring it up to your screen's. The HUD stays at full resolution. `Off` returns the game's own rendering.
- AMD FSR upscaling for every other graphics card, with the same modes (DLAA becomes FSR's Native AA): FSR 4 on the AMD Radeon RX 7000 and 9000 series, FSR 3.1 elsewhere. The Upscaler setting (Auto, DLSS, FSR) picks it; Auto keeps DLSS on NVIDIA RTX cards, and a pick the card can't run falls back to Auto.
- NVIDIA DLSS Frame Generation with NVIDIA Reflex (experimental, off by default): one generated frame between every two rendered frames. Needs an RTX 40 or 50 series card, Windows' hardware-accelerated GPU scheduling, windowed or borderless mode and a DLSS mode other than `Off` (DLAA counts). It switches on and off in the game at once, with no restart: the mod routes every frame through a Direct3D 12 step from the start of the game (a little extra cost even with Frame Generation off), and leaves the game's own presentation alone on a card that cannot run it.
- AMD FSR Frame Generation (experimental, off by default) for every card that cannot run DLSS Frame Generation (AMD, Intel, GTX, RTX 20 and 30 series), and on any card where you pick it; it runs with either upscaler. It has no Reflex. Frame Generation is its own setting (Off, Auto, DLSS, FSR), apart from the upscaler, and switching between DLSS and FSR Frame Generation takes effect at once while you play.
- Frame Generation composites the HUD and crosshair onto generated frames as a layer of their own (can be switched off), and pauses while the map or the menu is open or you use a terminal (its text would ghost).
- NVIDIA Reflex can run as On or On + Boost, with an optional frame rate limit.
- DLSS options: the model (Auto, K, M, L), and automatic or fixed exposure.
- Every setting sits in the game's own settings, at the end of Video > Advanced Graphics, applies at once and follows the game's RESET TO DEFAULT; hold the cursor on a setting for a hint, and on a greyed-out one for what it needs.
