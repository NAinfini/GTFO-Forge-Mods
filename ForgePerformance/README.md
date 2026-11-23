# Forge Performance

Cuts visual work in GTFO to raise the frame rate: corpses, ambient dust, far decoration, decals, shadows, rooms behind closed doors and similar. Every cut has its own switch, and with every cut off the plugin does nothing.

All switches are in `BepInEx/config/NAinfini.ForgePerformance.cfg`, section `[Performance]`; restart the game after editing. A few defaults change play or looks (corpses stop blocking shots sooner, far rooms behind closed doors are not drawn, enemies far away look coarser); the developer guide lists each one and how to turn it off.

It does not change damage, hit detection, enemy count or spawning, objectives, level layout or player controls. No frame-rate gain has been measured yet.

Install with r2modman or the Thunderstore Mod Manager (BepInExPack_GTFO and GTFO_API come with it). No Forge package is needed.

Report bugs on [GitHub](https://github.com/NAinfini/GTFO-Forge-Mods/issues), or find me on the GTFO Modding Discord as **nainfini**. Settings, measurements and the build: [developer guide](https://github.com/NAinfini/GTFO-Forge-Mods/blob/main/ForgePerformance/DEVELOPERS.md).

[MIT license](https://github.com/NAinfini/GTFO-Forge-Mods/blob/main/ForgePerformance/LICENSE).