"""Summarize one bounded owner_native capture; never compare process addresses across runs."""
import argparse
from collections import Counter
import json
from pathlib import Path


def summarize(directory: Path) -> dict:
    rows = []
    for path in sorted(directory.glob("refresh-*.jsonl")):
        for line in path.read_text(encoding="utf-8").splitlines():
            row = json.loads(line)
            if row.get("kind") == "native_owner_candidate":
                rows.append(row)
    if not rows:
        raise ValueError("No native-owner candidates in this capture")
    phases = {(row["session"], row["request"]) for row in rows}
    if len(phases) != 1:
        raise ValueError("Refusing to combine addresses from different phases")
    by_object = {}
    for row in rows:
        if row.get("state") != "resolved_candidate":
            continue
        address = row["object"]
        if address in by_object:
            raise ValueError("Duplicate object observation; resolve lifetime ambiguity first")
        by_object[address] = row
    avatars = []
    custom = {
        addr for addr, row in by_object.items()
        if row["resource_hash"].startswith("5236bd49853b")
    }
    for addr, row in by_object.items():
        if row["resource_hash"] != "4d1c334d294dfa97":
            continue
        if row.get("association_state") != "complete":
            continue
        children = row.get("association_objects", "").split(";")
        children = [child for child in children if child]
        if len(children) != row["association_count"] or len(set(children)) != len(children):
            raise ValueError("Invalid completed association list")
        matched = set(children) & custom
        avatars.append({
            "object": addr,
            "association_count": len(children),
            "matched_custom_count": len(matched),
            "other_custom_instances_not_in_list": len(custom - matched),
            "children": [
                {"object": child,
                 "resource_hash": by_object.get(child, {}).get("resource_hash")}
                for child in children
            ],
        })
    session, request = next(iter(phases))
    return {
        "schema": 1, "session": session, "request": request,
        "candidates": len(rows),
        "resolution_states": dict(Counter(row["state"] for row in rows)),
        "association_states": dict(Counter(row.get("association_state", "absent") for row in rows)),
        "avatars": avatars,
        "atomic_snapshot": False,
        "local_player_authority_proven": False,
        "refresh_entry_found": False,
        "note": "Membership uses same-phase native pointers, not ordinals or equal resource IDs.",
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    args = parser.parse_args()
    result = summarize(args.capture)
    text = json.dumps(result, ensure_ascii=False, indent=2)
    (args.capture / "native-association-summary.json").write_text(text + "\n", encoding="utf-8")
    print(text)
