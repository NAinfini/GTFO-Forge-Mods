# ForgeCore

A library other GTFO mods depend on. It does nothing on its own: install it when a mod asks for it (r2modman does this for you), such as [Forge Ordnance](https://thunderstore.io/c/gtfo/p/NAinfini/ForgeOrdnance/). With no mod using it, the game looks and sounds exactly as it does without it.

It gives mods one shared base for networking, keys, settings, the HUD and menus, sound and visual effects, so several mods never cover each other or the game's own HUD. Players running a different version of a mod are refused on both sides and told.

Mod keys are listed in the game's Controls page, mod volumes under the game's Audio sliders (MOD AUDIO), and everything else is in `BepInEx/config/NAinfini.ForgeCore.cfg`. Requires BepInExPack_GTFO and GTFO_API.

Report bugs on [GitHub](https://github.com/NAinfini/GTFO-Forge-Mods/issues), or find me on the GTFO Modding Discord as **nainfini**. For mod authors: [developer guide](https://github.com/NAinfini/GTFO-Forge-Mods/blob/main/ForgeCore/DEVELOPERS.md).

## License

Code: [MIT](https://github.com/NAinfini/GTFO-Forge-Mods/blob/main/ForgeCore/LICENSE). It includes NVorbis (MIT) to decode Ogg Vorbis; its license ships in the package.

Fonts, under the [SIL Open Font License 1.1](https://openfontlicense.org) (full texts in `fonts/`):

- Rajdhani, © 2014 Indian Type Foundry.
- Glow Sans SC Condensed (未来荧黑), © 2021 Project Welai; the package carries its common-character subset.