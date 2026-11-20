# Changelog

## 1.0.0

- NVIDIA DLSS Super Resolution in place of the game's own anti-aliasing: DLAA at your screen's resolution, or Quality, Balanced, Performance and Ultra Performance, which render the world smaller and bring it up to your screen's. The HUD stays at full resolution. `Off` returns the game's own rendering.
- AMD FSR upscaling for every other graphics card, with the same modes (DLAA becomes FSR's Native AA): FSR 4 on the AMD Radeon RX 7000 and 9000 series, FSR 3.1 elsewhere. The Upscaler setting (Auto, DLSS, FSR) picks it; Auto keeps DLSS on NVIDIA RTX cards, and a pick the card can't run falls back to Auto.
- NVIDIA DLSS Frame Generation with NVIDIA Reflex (experimental, off by default): one generated frame between every two rendered frames. Needs an RTX 40 or 50 series card, Windows' hardware-accelerated GPU scheduling, windowed or borderless mode and a DLSS mode other than `Off` (DLAA counts). It switches on and off in the game at once, with no restart: the mod routes every frame through a Direct3D 12 step from the start of the game (a little extra cost even with Frame Generation off), and leaves the game's own presentation alone on a card that cannot run it.
- AMD FSR Frame Generation (experimental, off by default) for every card that cannot run DLSS Frame Generation (AMD, Intel, GTX, RTX 20 and 30 series), and on any card where you pick it; it runs with either upscaler. It has no Reflex. Frame Generation is its own setting (Off, Auto, DLSS, FSR), apart from the upscaler, and switching between DLSS and FSR Frame Generation takes effect at once while you play.
- Frame Generation composites the HUD and crosshair onto generated frames as a layer of their own (can be switched off), and pauses while the map or the menu is open or you use a terminal (its text would ghost).
- NVIDIA Reflex can run as On or On + Boost, with an optional frame rate limit.
- DLSS options: the model (Auto, K, M, L), and automatic or fixed exposure.
- Every setting sits in the game's own settings, at the end of Video > Advanced Graphics, applies at once and follows the game's RESET TO DEFAULT; hold the cursor on a setting for a hint, and on a greyed-out one for what it needs.
