"""Compares the enemy replication tables of two machines (hot reload tier D, host after a generation change against a machine that joined
afterwards). Each side is the run folder of `scripts/reload-d-replication-table.test`; the table is the `entries` command's result: the
`index:id` pairs the game copies enemy prefabs by, and their digest. Exit 0 when equal, 1 when they differ (the first differing index is
named), 2 when a folder holds no table."""
from __future__ import annotations

import json
from pathlib import Path
import sys


def table(folder):
    """The flat values of the last `entries` command of a console run folder."""
    for result in sorted(Path(folder).glob("*-entries/result.json"), reverse=True):
        row = json.loads(result.read_text("utf-8"))
        if row.get("ok") and "replication" in row.get("values", {}):
            return row["values"]
    raise ValueError("no successful `entries` result in " + str(folder))


def differences(host, client):
    """What differs between two tables, as text lines; empty when the two machines copy enemies by the same indexes."""
    rows = []
    left, right = host["replication"].split(";"), client["replication"].split(";")
    for index in range(max(len(left), len(right))):
        a, b = left[index] if index < len(left) else "(none)", right[index] if index < len(right) else "(none)"
        if a != b:
            rows.append("position " + str(index) + ": host " + a + ", client " + b)
    for key in ("replicationDigest", "replicationCount", "vanillaCount", "ownOrder"):
        if host.get(key) != client.get(key):
            rows.append(key + ": host " + str(host.get(key)) + ", client " + str(client.get(key)))
    return rows


def main(argv):
    if len(argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    try:
        host, client = table(argv[0]), table(argv[1])
    except (OSError, ValueError) as error:
        print(error, file=sys.stderr)
        return 2
    rows = differences(host, client)
    print("\n".join(rows) if rows else "equal: " + host["replicationCount"].__str__() + " entries, digest " + str(host["replicationDigest"]))
    return 1 if rows else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
