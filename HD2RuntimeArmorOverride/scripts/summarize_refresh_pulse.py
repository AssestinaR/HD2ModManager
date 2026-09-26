"""Validate one opt-in pending-byte experiment from logs, offline only."""
import argparse
import json
import struct
from pathlib import Path


def summarize(directory):
    rows = []
    for path in directory.glob("refresh-*.jsonl"):
        rows.extend(json.loads(line) for line in path.read_text(encoding="utf-8").splitlines())
    rows = [r for r in rows if r.get("phase") == "preflight_route_pulse"]
    if len({(r["session"], r["request"]) for r in rows}) != 1:
        raise ValueError("Expected one pulse request")
    if any(r["kind"] == "code_survey_error" for r in rows):
        raise ValueError("Failed experiment")
    def one(kind):
        found = [r for r in rows if r["kind"] == kind]
        if len(found) != 1:
            raise ValueError(f"Expected exactly one {kind}")
        return found[0]
    intent, write, end = one("pulse_intent"), one("pulse_written"), one("code_survey_end")
    if not end["complete"] or write["byte_count"] != 1 or end["writes"] != 1:
        raise ValueError("Incomplete or non-single-byte experiment")
    observations = [r for r in rows if r["kind"] == "pulse_observation"]
    if len(observations) < 2 or observations[-1]["after_ms"] < 3000:
        raise ValueError("Insufficient post-trigger observation")
    before = struct.unpack("<30I", bytes.fromhex(intent["output_hex"])[:120])
    states = set()
    for row in observations:
        if row["dirty"] != 0 or not row["requested_unchanged"]:
            raise ValueError("Not consumed or equipment request changed")
        # Check actual bytes as well as the probe's boolean.
        if row["applied_hex"] != intent["applied_hex"]:
            raise ValueError("Applied record not restored exactly")
        states.add(row["output_hex"])
    if len(states) != 1:
        raise ValueError("Post-trigger outputs did not settle")
    after = struct.unpack("<30I", bytes.fromhex(observations[-1]["output_hex"])[:120])
    changed = [i for i, (a, b) in enumerate(zip(before, after)) if a != b]
    return {
        "session": rows[0]["session"], "revision": intent["revision"],
        "writes": 1, "native_candidate_calls": 0,
        "first_observed_rebuild_ms": observations[0]["after_ms"],
        "latency_is_upper_bound_not_exact": True,
        "observed_until_ms": observations[-1]["after_ms"],
        "observations": len(observations), "flag_consumed": True,
        "requested_state_unchanged": True, "applied_record_exactly_unchanged": True,
        "changed_output_indices": changed,
        "nonzero_before": sum(bool(v) for v in before),
        "nonzero_after": sum(bool(v) for v in after),
        "old_new_nonzero_overlap": sorted((set(before) & set(after)) - {0}),
        "output_values_are_not_verified_lua_handles": True,
        "custom_mapping_refresh_tested": False,
        "visual_confirmation": False, "complete_gameplay_state_audited": False,
        "production_safety_proven": False,
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    result = summarize(args.directory)
    text = json.dumps(result, indent=2)
    (args.directory / "pulse-analysis.json").write_text(text + "\n", encoding="utf-8")
    print(text)
