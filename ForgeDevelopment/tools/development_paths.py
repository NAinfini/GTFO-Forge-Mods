"""Shared paths for ForgeDevelopment author data outside BepInEx/config."""
from pathlib import Path


def development_root(bepinex_root: str | Path) -> Path:
    return Path(bepinex_root) / "ForgeDevelopment"
