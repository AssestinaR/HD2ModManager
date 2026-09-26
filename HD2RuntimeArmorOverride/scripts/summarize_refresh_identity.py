"""Cross-check one read-only identity capture; never connects to the game."""
import argparse
import json
import struct
from pathlib import Path


def summarize(directory):
    rows = []
    for path in directory.glob("refresh-*.jsonl"):
        rows.extend(json.loads(line) for line in path.read_text(encoding="utf-8").splitlines())
    rows = [r for r in rows if r.get("phase") == "preflight_route_identity"]
    if len({(r["session"], r["request"]) for r in rows}) != 1:
        raise ValueError("Expected one identity capture")
    if any(r["kind"] == "code_survey_error" for r in rows):
        raise ValueError("Probe error")
    end = [r for r in rows if r["kind"] == "code_survey_end" and r.get("complete")]
    if len(end) != 1 or end[0].get("identity_format") != 1:
        raise ValueError("Incomplete identity capture")
    snapshots = [r for r in rows if r["kind"] == "identity_snapshot"]
    if len(snapshots) != 2 or {r["sample"] for r in snapshots} != {0, 1}:
        raise ValueError("Expected two snapshots")
    results = []
    for snap in snapshots:
        sample = snap["sample"]
        if not snap["stable"] or not snap.get("local_entity_id"):
            raise ValueError("Unstable snapshot or local avatar not yet spawned")
        instances = [r for r in rows if r["kind"] == "identity_instance" and r["sample"] == sample]
        components = [r for r in rows if r["kind"] == "identity_component" and r["sample"] == sample]
        if len(instances) != snap["instance_count"] or len(components) != snap["component_count"]:
            raise ValueError("Incomplete scalar records")
        matches = [r for r in instances if r.get("matches_local")]
        if len(matches) != 1:
            raise ValueError("Local instance not unique")
        instance = matches[0]
        matches = [r for r in components if r["index"] == instance.get("component_index")]
        if len(matches) != 1:
            raise ValueError("Component index not unique")
        component = matches[0]
        for obj in (instance, component):
            if not obj["stable"] or obj["entity_hex"] != snap["local_entity_hex"]:
                raise ValueError("Entity identity mismatch")
        if not instance["reverse_entity_matches"] or not component["matches_local"]:
            raise ValueError("Reverse lookup mismatch")
        entity = bytes.fromhex(snap["local_entity_hex"])
        _, identity, _, engine_unit, flags = struct.unpack("<QIIII", entity)
        if identity != snap["local_entity_id"] or engine_unit != snap["local_unit"]:
            raise ValueError("Entity fields inconsistent")
        if engine_unit != instance["player_unit"] or engine_unit != component["engine_unit"]:
            raise ValueError("Engine Unit mismatch")
        if instance["entry_hex"] != snap["player_record_hex"]:
            raise ValueError("Player descriptor mismatch")
        output = bytes.fromhex(component["output_hex"])
        # Three 0x28-byte buffers from reviewed current code. Scalar values
        # are NOT yet proven to be Lua handles or linked-child identities.
        buffers = [list(struct.unpack_from("<10I", output, start)) for start in (0, 40, 80)]
        results.append({
            "sample": sample, "instance_index": instance["index"],
            "instance_key": instance["key08"], "player_index": instance["player_index"],
            "entity_id": identity, "engine_unit": engine_unit, "entity_flags": flags,
            "component_index": component["index"], "dirty": instance["dirty"],
            "body_flag": component["body_flag"], "output_buffers": buffers,
            "output_nonzero_count": sum(v != 0 for b in buffers for v in b),
            "identity_cross_check": True, "child_handle_join_proven": False,
        })
    if {k: v for k, v in results[0].items() if k != "sample"} != {
        k: v for k, v in results[1].items() if k != "sample"
    }:
        raise ValueError("Identity/output changed between snapshots")
    return {
        "session": rows[0]["session"], "snapshots": results,
        "refresh_implemented": False, "safe_native_call_proven": False,
        "atomic_snapshot": False,
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    result = summarize(args.directory)
    text = json.dumps(result, indent=2, ensure_ascii=False)
    (args.directory / "identity-analysis.json").write_text(text + "\n", encoding="utf-8")
    print(text)
