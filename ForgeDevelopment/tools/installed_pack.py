"""The installed pack as the drive tools read it: the bytes of its forge/ folder, its package identity and its enabled levels."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

import drive_session as ds

MAX_JSON_BYTES = 64 * 1024 * 1024


def derived_level_label(level_id):
    if level_id is None: return ""
    if not isinstance(level_id, str) or not level_id:
        raise ValueError("invalid-level-address: the derived check has an invalid website levelId")
    parts = level_id.split(":")
    if (len(parts) != 3 or not parts[0] or len(parts[1]) != 1 or parts[1] not in "ABCDE"
            or not parts[2].isascii() or not parts[2].isdigit()):
        raise ValueError("invalid-level-address: the derived check has an invalid website levelId " + level_id)
    rundown, tier, ordinal = parts
    return rundown + "-" + tier + str(int(ordinal) + 1)


def content_sha256(files):
    """The plugin's identity of a pack: SHA-256 over the ordinally sorted `path\\nsha256\\n` lines of (path, sha256) rows."""
    ordered = sorted(files, key=lambda row: row[0].encode("utf-16-be"))
    return hashlib.sha256("".join(path + "\n" + digest + "\n" for path, digest in ordered).encode("utf-8")).hexdigest()


def installed_files(plugin):
    """(path, sha256) of every file under the installed pack's forge/ folder, paths relative to the plugin folder."""
    return [(path.relative_to(plugin).as_posix(), hashlib.sha256(path.read_bytes()).hexdigest())
            for path in sorted((plugin / "forge").rglob("*")) if path.is_file()]


def json_file(plugin, relative, required=False, expected_sha256=None):
    path = plugin / relative
    if not path.is_file():
        if required: raise ValueError("required package file is missing: " + relative.as_posix())
        return None
    try:
        with path.open("rb") as stream: data = stream.read(MAX_JSON_BYTES + 1)
        if len(data) > MAX_JSON_BYTES: raise ValueError("package JSON exceeds its byte budget")
        if expected_sha256 is not None and hashlib.sha256(data).hexdigest() != expected_sha256:
            raise ValueError("installed package bytes changed after locking")
        return json.loads(data.decode("utf-8"), object_pairs_hook=ds.transport.unique_object)
    except (OSError, ValueError) as error:
        raise ValueError("invalid package JSON " + relative.as_posix() + ": " + str(error)) from error


def installed_package_id(plugin):
    """Reads the package identity from the same installed declaration the native runner accepts."""
    rundown = json_file(plugin, Path("forge/rundown.forge"))
    content = json_file(plugin, Path("forge/content/pack.forge"))
    declaration = rundown if rundown is not None else content
    expected_format = "forge-rundown-pack" if rundown is not None else "forge-content-pack"
    if not isinstance(declaration, dict) or declaration.get("format") != expected_format:
        raise ValueError("installed package declaration is missing or has the wrong format")
    package_id = declaration.get("package")
    if not isinstance(package_id, str) or not package_id or Path(package_id).name != package_id:
        raise ValueError("installed package declaration has an invalid package id")
    return package_id


def installed_level_declarations(plugin, installed, package_id):
    """Read all enabled levels from the locked declaration, including levels with no derived checks."""
    relative = Path("forge/rundown.forge")
    digest = dict(installed).get(relative.as_posix())
    if digest is None:
        if (plugin / relative).exists(): raise ValueError("installed Rundown appeared after locking")
        return []
    declaration = json_file(plugin, relative, required=True, expected_sha256=digest)
    if (not isinstance(declaration, dict) or declaration.get("format") != "forge-rundown-pack"
            or declaration.get("package") != package_id or not isinstance(declaration.get("blocks"), list)):
        raise ValueError("invalid installed Rundown declaration")
    issued = declaration.get("ids")
    if not isinstance(issued, dict): raise ValueError("installed Rundown declaration issues no ids")
    blocks, numbers, numeric_ids = {}, {}, set()
    for row in declaration["blocks"]:
        if (not isinstance(row, dict) or any(not isinstance(row.get(name), str) or not row[name].strip() for name in ("id", "type"))
                or not isinstance(row.get("data"), dict)):
            raise ValueError("invalid Rundown block identity or data")
        block_id, kind = row["id"], row["type"]
        number = issued.get(block_id)
        if not isinstance(number, int) or isinstance(number, bool): raise ValueError("installed Rundown issues no number for " + block_id)
        if block_id in blocks or (kind, number) in numeric_ids:
            raise ValueError("duplicate or colliding Rundown block identity: " + block_id)
        blocks[block_id], numbers[block_id] = row, number
        numeric_ids.add((kind, number))
    rundown_id = declaration.get("rundown")
    if not isinstance(rundown_id, str) or rundown_id not in blocks or blocks[rundown_id]["type"] != "Rundown":
        raise ValueError("Rundown declaration must name one installed Rundown block")
    levels, seen = [], set()
    for tier in "ABCDE":
        entries = blocks[rundown_id]["data"].get("Tier" + tier, [])
        if not isinstance(entries, list): raise ValueError("Rundown Tier" + tier + " must be an array")
        for index, entry in enumerate(entries):
            address = "Tier" + tier + "[" + str(index) + "]"
            if not isinstance(entry, dict) or "Enabled" in entry and not isinstance(entry["Enabled"], bool):
                raise ValueError("invalid expedition or Enabled flag: " + address)
            if not entry.get("Enabled", True): continue
            # Layout admission belongs to Native; it does not change the level identity.
            descriptive = entry.get("Descriptive")
            if descriptive is None: descriptive = {}
            if (not isinstance(descriptive, dict) or descriptive.get("PublicName") is not None and not isinstance(descriptive["PublicName"], str)):
                raise ValueError("invalid expedition public name: " + address)
            label = derived_level_label(str(numbers[rundown_id]) + ":" + tier + ":" + str(index))
            item_id = package_id + "/levels/" + label
            if item_id in seen: raise ValueError("duplicate enabled level identity: " + item_id)
            seen.add(item_id)
            levels.append({"itemId": item_id, "name": descriptive.get("PublicName") if isinstance(descriptive.get("PublicName"), str) else label})
    return levels
