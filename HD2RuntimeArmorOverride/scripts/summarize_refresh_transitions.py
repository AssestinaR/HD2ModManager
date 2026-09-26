"""Summarize bounded avatar association observations, not native call traces."""
import argparse
import json
from collections import Counter
from pathlib import Path


def summarize(directory: Path) -> dict:
    phases = []
    sessions, pids, revisions = set(), set(), set()
    for path in sorted(directory.glob("refresh-*.jsonl")):
        records = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line]
        starts = [r for r in records if r.get("kind") == "phase_begin" and r.get("mode") == "owner_trace"]
        if not starts:
            continue
        if len(starts) != 1:
            raise ValueError("Expected exactly one owner_trace phase per file")
        start = starts[0]
        sessions.add(start["session"])
        pids.add(start["pid"])
        revisions.add(start["registry_revision"])
        rows = [r for r in records if r.get("kind") == "native_owner_trace"]
        previous = {}
        observations = []
        last_time = -1
        for row in rows:
            if (row["session"], row["request"], row["phase"]) != (
                start["session"], start["request"], start["phase"]
            ):
                raise ValueError("Mixed phase/session in trace")
            if row["seconds"] < last_time:
                raise ValueError("Non-monotonic capture")
            last_time = row["seconds"]
            item = {k: row.get(k) for k in (
                "seconds", "sample_index", "state", "association_state",
                "object", "object_world", "handle_index", "handle_generation"
            )}
            complete = row.get("state") == "resolved_candidate" and row.get("association_state") == "complete"
            if complete:
                children = [x for x in row.get("association_objects", "").split(";") if x]
                if len(children) != row["association_count"] or len(children) != len(set(children)):
                    raise ValueError("Malformed complete child list")
                key = (row["object_world"], row["object"], row["handle_index"], row["handle_generation"])
                prior = previous.get(key)
                item["children"] = children
                item["comparison"] = "first_complete_observation"
                if prior is not None:
                    item["comparison"] = "non_atomic_same_key"
                    item["links_added"] = sorted(set(children) - set(prior))
                    item["links_removed"] = sorted(set(prior) - set(children))
                    item["order_changed"] = children != prior
                    item["membership_changed"] = set(children) != set(prior)
                previous[key] = children
            else:
                # A failed observation breaks continuity; do not compare across it.
                previous.clear()
            observations.append(item)
        ends = [r for r in records if r.get("kind") == "phase_end"]
        phases.append({
            "request": start["request"], "phase": start["phase"], "file": path.name,
            "complete_log": len(ends) == 1 and ends[0].get("truncated") is False,
            "states": dict(Counter(r["state"] for r in rows)),
            "observations": observations,
        })
    if not phases:
        raise ValueError("No owner_trace phase found")
    if len(sessions) != 1 or len(pids) != 1 or len(revisions) != 1:
        raise ValueError("Refusing mixed sessions, processes or registry revisions")
    return {
        "schema": 1, "session": next(iter(sessions)), "pid": next(iter(pids)),
        "registry_revision": next(iter(revisions)),
        "phases": sorted(phases, key=lambda p: p["request"]),
        "refresh_entry_found": False, "local_player_authority_proven": False,
        "note": "Pointer-list deltas only. No deletion, lifetime, call-order or controller proof. "
                "No comparisons across phases or failed reads; address/generation reuse remains possible. "
                "User action cues must be checked separately in phase-markers.jsonl.",
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    args = parser.parse_args()
    result = summarize(args.capture)
    text = json.dumps(result, ensure_ascii=False, indent=2)
    (args.capture / "native-transition-summary.json").write_text(text + "\n", encoding="utf-8")
    print(text)
