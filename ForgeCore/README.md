# ForgeCore

A library other GTFO mods depend on. It does nothing on its own: install it when a mod asks for it (r2modman does this for you).

It gives mods one shared base for talking over the network (a player running a different version of a mod is refused on both sides and told), taking over keys, logging, keeping settings and local progress, pooling objects, and calling the game's own damage, explosion, gear and data block systems.

Report bugs on [GitHub](https://github.com/NAinfini/GTFO-Forge-Mods/issues), or find me on the GTFO Modding Discord as **nainfini**.

## Installation

Install with r2modman or the Thunderstore Mod Manager. The only dependency is BepInExPack_GTFO.

## For mod authors

The quick start and the list of public types are in the [developer guide](https://github.com/NAinfini/GTFO-Forge-Mods/blob/main/ForgeCore/DEVELOPERS.md).

## License

[MIT](https://github.com/NAinfini/GTFO-Forge-Mods/blob/main/ForgeCore/LICENSE).
