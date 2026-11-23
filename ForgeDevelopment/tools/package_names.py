"""The authored names of an installed package's rooms, so a test script says `teleport room=机关样例区 ...` and never a generated id.

The game only knows a room by the instance id the Map domain wrote (`room:<hash>` or `room-<uuid>`); the name the author gave it lives in
the package: forge-project.forge lists every room with the room resource it was placed from, and that resource (forge/resources/*.room.forge)
holds the name. Several placed rooms may share one resource, and one name may exist in several of the package's expeditions, so a name answers
every instance id that carries it; the console keeps the ones the loaded level actually has (console_commands.Console.room_id).
A name is the whole text or one side of an `中文 / English` (or `中文 · English`) pair, compared without case."""
from __future__ import annotations

import json
from pathlib import Path
import re

PAIR = re.compile(r"\s+[/／·]\s+")


def plugin_folder(state):
    selection = state.get("selection") or {}
    if not selection.get("pluginDirectory"):
        raise ValueError("console.room-name-needs-package: 会话没有安装包（vanilla 关卡），房间只能写实例 id")
    return Path(state["bepinexRoot"]) / "plugins" / selection["pluginDirectory"]


def read(path):
    return json.loads(Path(path).read_text("utf-8-sig"))


def room_names(plugin):
    """{instance id: authored name} of every room the package places."""
    resource_names = {}
    for path in (plugin / "forge" / "resources").glob("*.room.forge"):
        value = read(path)
        if isinstance(value.get("id"), str) and isinstance(value.get("name"), str):
            resource_names[value["id"]] = value["name"]
    names = {}
    for reference in read(plugin / "forge-project.forge").get("objectReferences", []):
        if reference.get("kind") != "room":
            continue
        locator = reference.get("locator") or {}
        resource = ((locator.get("room") or {}).get("id") or "").split(":", 1)[-1]
        if resource in resource_names:
            names[locator.get("instanceId") or reference["authorId"]] = resource_names[resource]
    return names


def parts(name):
    return [name.strip().casefold()] + [part.strip().casefold() for part in PAIR.split(name)]


def room_ids(state, wanted):
    """Every instance id of the installed package whose room is called `wanted`; ValueError (naming the closest names) when none is."""
    names = room_names(plugin_folder(state))
    key = wanted.strip().casefold()
    found = [instance for instance, name in names.items() if key in parts(name)]
    if not found:
        near = sorted({name for name in names.values() if key in name.casefold()})[:8]
        raise ValueError("console.room-unknown: 安装包里没有名为“" + wanted + "”的房间" + ("；名字里含它的有：" + "、".join(near) if near else ""))
    return found
