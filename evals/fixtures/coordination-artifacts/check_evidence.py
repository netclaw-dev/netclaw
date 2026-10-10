"""Check an actual raw catalog dependency without any repair operation."""

import json
from pathlib import Path
import sys


def check(root, nonce, display_root):
    manifest = json.loads((root / "manifest.json").read_bytes())
    dependency = root / "raw-catalog.json"
    result = {"nonce": nonce, "snapshot_id": manifest["snapshot_id"], "measurement": "active_record_count"}
    if not dependency.exists():
        return 2, {**result, "status": "unavailable", "missing_dependency": display_root + "/raw-catalog.json",
                   "active_record_count": None}
    data = json.loads(dependency.read_bytes())
    if data["snapshot_id"] != manifest["snapshot_id"]:
        raise ValueError("The raw catalog belongs to another snapshot.")
    return 0, {**result, "status": "verified", "missing_dependency": None,
               "active_record_count": sum(record["active"] is True for record in data["records"])}


if __name__ == "__main__":
    code, result = check(Path(__file__).resolve().parent, sys.argv[1], sys.argv[2])
    print(json.dumps(result, sort_keys=True))
    raise SystemExit(code)
