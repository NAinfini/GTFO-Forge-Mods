"""The hand-off of an installed package to the running game, as the workshop does it (contract §3.2 I-RELOAD, "工坊与游戏的交接";
the TypeScript side is `handOver` in Desktop/workshop/src/main/main.ts, and this file writes and reads the same two files).

The package is already in the profile with its record `.forge-installed.json` written last. The request names the package and the SHA-256
of that record and is written to a temporary name and renamed into place; the game answers it once in `result.json`. A game that is in a
level answers `leaving` first, goes back to its lobby and answers again."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import time
import uuid

FOLDER = "forge-reload"
RECORD = ".forge-installed.json"
ANSWER_SECONDS = 60
LEAVE_SECONDS = 90  # after a "leaving" answer: the game's own 60 s way back, the 10 s for a clean lobby, and the reload


def game_running(bepinex):
    """True while a game that answers requests runs from this profile: it holds forge-reload/game.lock open without sharing."""
    try:
        with open(Path(bepinex) / FOLDER / "game.lock", "r+b"):
            return False
    except FileNotFoundError:
        return False
    except PermissionError:
        return True


def read_result(bepinex):
    try:
        value = json.loads((Path(bepinex) / FOLDER / "result.json").read_text("utf-8"))
    except (OSError, ValueError):
        return None  # no answer yet, or the game is renaming it into place: read again
    return value if isinstance(value, dict) and value.get("format") == "forge.reload-result" else None


def hand_over(bepinex, packages, sleep=time.sleep):
    """Writes one request for `packages` and waits for the game's answer; returns `(request id, {package: record}, result)`. A record is what
    the package's .forge-installed.json hashes to now; the caller compares it with the record the result names for the package."""
    bepinex = Path(bepinex)
    records = {package: hashlib.sha256((bepinex / "plugins" / package / RECORD).read_bytes()).hexdigest() for package in packages}
    if not game_running(bepinex):
        raise RuntimeError("reload-game-not-running: no game answers requests from " + str(bepinex))
    folder = bepinex / FOLDER
    folder.mkdir(parents=True, exist_ok=True)
    request, identity = folder / "request.json", str(uuid.uuid4())
    Path(str(request) + ".tmp").write_text(json.dumps({"format": "forge.reload-request", "formatVersion": 1, "id": identity,
                                                         "packages": [{"package": package, "record": record} for package, record in records.items()]}, indent=2), "utf-8")
    os.replace(str(request) + ".tmp", request)
    leaving, deadline = False, time.monotonic() + ANSWER_SECONDS
    while time.monotonic() < deadline:
        sleep(0.5)
        result = read_result(bepinex)
        if result is None or result.get("request") != identity:
            continue
        if result.get("status") != "leaving":
            return identity, records, result
        if not leaving:
            leaving, deadline = True, time.monotonic() + LEAVE_SECONDS
    if not leaving:
        request.unlink(missing_ok=True)  # nobody answered: taken back so a later start does not see it
    raise TimeoutError("reload-no-answer: request " + identity + " for " + ", ".join(records) + (" was not answered after the game went back to its lobby" if leaving else " was not answered"))


def summary(identity, records, result):
    """The flat values a console line keeps (`as=name`): the game's answer in the fields a script judges."""
    own = {row.get("package"): row for row in result.get("packages", [])}
    rejections = [row for package in records for row in own.get(package, {}).get("rejections", [])]
    return {"request": identity, "status": result.get("status"), "code": result.get("code") or "", "detail": result.get("detail") or "",
            "generation": result.get("generation"), "digest": result.get("digest") or "", "recordMatches": all(own.get(package, {}).get("record") == record for package, record in records.items()),
            "rejections": len(rejections), "rejected": "; ".join(row.get("layer", "") + ":" + row.get("code", "") + ":" + row.get("document", "") for row in rejections),
            "rejoinState": (result.get("rejoin") or {}).get("state", "")}


def flatten_entries(document, match=""):
    """The flat values of the author read `ForgeEngine.Authoring.AuthorReads.EntryRows` (the enemy rows, the replication list in index
    order and the weapon gear rows), as the console keeps them. `forgeOrder` writes each Forge enemy in replication order as E (a content
    entry), R (a Rundown row) or O; `ownOrder` the same for the Forge enemies whose name contains `match` (one test's own packages among
    the other Forge content a profile holds). The name lists are joined with `|`; `replication` is `index:id` pairs joined with `;`."""
    enemy, weapon = document["enemy"], document["weapon"]
    source = {row["id"]: row["source"] for row in enemy["rows"]}
    forge = enemy["forge"]
    tag = lambda rows: "".join(source.get(row["id"], "O")[0].upper() for row in rows)
    return {"generation": document["generation"], "replicationDigest": document["replicationDigest"],
            "replicationCount": enemy["replicationCount"], "vanillaCount": enemy["replicationCount"] - len(forge),
            "forgeOrder": tag(forge), "ownOrder": tag(row for row in forge if match in row["name"]),
            "enemyRows": len(enemy["rows"]), "enemyRowsIncomplete": sum(1 for row in enemy["rows"] if not (row["prefab"] and row["spawn"] and row["index"] >= 0)),
            "entryEnemies": "|".join(enemy["entryNames"]), "rundownEnemies": "|".join(enemy["rundownNames"]),
            "weaponRows": len(weapon["rows"]), "weaponRowsIncomplete": sum(1 for row in weapon["rows"] if not (row["catalogued"] and row["slot"] >= 0)),
            "weaponEntries": "|".join(weapon["entryNames"]),
            "replication": enemy["replication"]}
