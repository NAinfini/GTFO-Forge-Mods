# Changelog

## 0.0.4 (beta)

- The full source code is now on [GitHub](https://github.com/NAinfini/GTFO-Forge-Mods/tree/main/ForgeWeapon_EnergyLab), including the Unity project that builds the effects.
- The weapon pictures on the mod page load again.
- No gameplay changes.

## 0.0.3 (beta)

- The Blast Gun and Beam have no range limit: a shot past 40 m (Blast Gun) or 45 m (Beam) now hits and explodes instead of doing nothing. The Chain Arc (16 m) and Flamethrower (8 m) keep their range.
- The Blast Gun no longer draws a beam-like line; the explosion appears where you aim.
- Switching weapons while holding aim no longer sends the sight's red dot flying across the screen; the dot shows only through the sight glass.
- Bots given an energy gun by an older version no longer spam icon errors in the log or show a squashed weapon icon after a restart, and their gun is rebuilt with the current sounds and settings.
- Tuning moved to `tuning-overrides.json`, which holds only the values you change; `tuning-defaults.json` lists every value. The old `tuning.json` is no longer read, so copy any values you changed.

## 0.0.2 (beta)

- The beam, flame and charging orb now stay on the muzzle while you strafe.
- Firing sounds play right away instead of slightly late, most noticeably on the Charge Orb.
- The Gravity Core's field stays visible as it collapses, and its collapse sound plays with it.

## 0.0.1 (beta)

- First release with eight energy guns: Beam, Chain Arc, Flamethrower, Charge Orb, Shock Orb, Blast Gun, Ricochet Disc and Gravity Core.
- Beam and Flamethrower have no recoil; the other six have light recoil.
- Back hits, weakspots and armor work the same as with normal guns.
- Bots can use all eight guns, with their effects and sounds.
- All numbers can be changed in `tuning.json`.
