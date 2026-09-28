"""Offline portable-report review. Never opens a process or changes a protocol.

The output-bank model describes ONE captured game.dll consumer, not every
preview/cleanup path. Aliasing is a testable lead, not a crash diagnosis.
"""
import argparse
from collections import Counter, defaultdict
from datetime import datetime, timezone, timedelta
import hashlib
import json
from pathlib import Path


ROUTE_SHA256 = "0327680cee1dd124b25f3542c493c57e5c07dc4bdb9a22fb647d19fb4c24e79b"
ROUTE_TIMESTAMP = "1790161983"
ROUTE_IMAGE_SIZE = "74727424"


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def read_lines(path):
    return [json.loads(line) for line in path.read_text(encoding="utf-8-sig").splitlines()
            if line.strip()]


def composition(pieces):
    """Protocol body encoding: 0 stocky, 1 slim, 3 any (not catalog enum)."""
    result = {}
    for body in (0, 1):
        active = [p for p in pieces if p.get("enabled", True) and p["body"] in (body, 3)]
        positions, banks = defaultdict(list), defaultdict(list)
        for p in active:
            slot, kind = p["slot"], p["pieceType"]
            positions[(slot, kind)].append(p["unit"])
            # Captured B path indexes three ten-DWORD buffers by slot.
            # Slot 0 is handled separately by A; do not generalize B to it.
            if 1 <= slot <= 9:
                banks[(slot, 0 if kind == 0 else 1 if kind == 1 else 2)].append(
                    {"unit": p["unit"], "pieceType": kind, "body": p["body"]})
        result[str(body)] = {
            "effectivePieces": len(active),
            "slotCounts": dict(sorted(Counter(p["slot"] for p in active).items())),
            "duplicatePositions": [
                {"slot": s, "pieceType": k, "units": ids}
                for (s, k), ids in sorted(positions.items()) if len(ids) > 1],
            "candidateOutputBankAliases": [
                {"slot": s, "bank": k, "pieces": entries}
                for (s, k), entries in sorted(banks.items()) if len(entries) > 1],
            "outsideCapturedBodySlotRange": [
                p["unit"] for p in active if not 1 <= p["slot"] <= 9],
        }
    return result


def summarize_samples(rows):
    matched, processes = [], {}
    stale, absent, unavailable = 0, 0, 0
    for row in rows:
        if row.get("state") == "sample_unavailable":
            unavailable += 1
            continue
        live = row.get("processes", [])
        status = row.get("runtime", {})
        if not live:
            absent += 1
        if status and not any(str(p["pid"]) == status.get("pid") for p in live):
            stale += 1
        for p in live:
            key = (p["pid"], p["startUtc"])
            entry = processes.setdefault(key, {
                "pid": p["pid"], "startUtc": p["startUtc"], "firstObserved": row["utc"],
                "lastObserved": row["utc"], "peakPrivateBytes": 0, "peakWorkingSetBytes": 0})
            entry["lastObserved"] = row["utc"]
            entry["peakPrivateBytes"] = max(entry["peakPrivateBytes"], p["privateMemoryBytes"])
            entry["peakWorkingSetBytes"] = max(entry["peakWorkingSetBytes"], p["workingSetBytes"])
            if str(p["pid"]) == status.get("pid"):
                matched.append(status)
    fields = ("revision", "state", "active_mappings", "last_writes",
              "retained_clone_count", "retained_clone_bytes", "game_timestamp", "game_image_size")
    return {
        "rows": len(rows), "unavailableRows": unavailable, "noProcessRows": absent,
        "unmatchedOrStaleStatusRows": stale, "matchingPidStatusRows": len(matched),
        "processes": list(processes.values()),
        "matchedStatusValues": {key: sorted({s[key] for s in matched if key in s}) for key in fields},
        "limits": "PID matching is not a runtime heartbeat; process presence does not prove health. No VRAM data.",
    }


def correlate_fault(event, samples):
    pid = int(event["ProcessId"], 16)
    ticks = int(event["ProcessCreationTime"], 16)
    # Python microsecond precision; preserve raw FILETIME in the event as well.
    start = datetime(1601, 1, 1, tzinfo=timezone.utc) + timedelta(microseconds=ticks // 10)
    matches = []
    for p in samples["processes"]:
        observed = datetime.fromisoformat(p["startUtc"].replace("Z", "+00:00"))
        if p["pid"] == pid and abs((observed - start).total_seconds()) < 0.001:
            matches.append(p["pid"])
    return {**event, "samplePidAndCreationTimeMatched": bool(matches)}


def review(report, route_window=None):
    summary = load(report / "summary.json")
    protocol = load(report / "protocol-summary.json")
    inventory = load(report / "inventory.json")
    deployed = {s.lower() for s in inventory["deployedUnitIds"]}
    samples = {stage: summarize_samples(read_lines(report / f"samples-{stage}.jsonl"))
               for stage in ("original", "disabled")}
    model_rows = []
    for model in protocol["models"]:
        pieces = model["pieces"]
        active = [p for p in pieces if p.get("enabled", True)]
        model_rows.append({
            "alias": model["alias"], "totalPieces": len(pieces),
            "missingDeployedUnitIds": sorted({p["unit"] for p in active if p["unit"].lower() not in deployed}),
            "bodies": composition(pieces),
        })
    events = {stage: load(report / f"events-{stage}.json") for stage in samples}
    faults = [correlate_fault(e, samples[stage]) for stage, bundle in events.items()
              for e in bundle.get("events", [])
              if e.get("eventId") == 1000 and "ProcessCreationTime" in e and "ProcessId" in e]
    verified = route_window is not None and hashlib.sha256(route_window.read_bytes()).hexdigest() == ROUTE_SHA256
    fingerprints = samples["original"]["matchedStatusValues"]
    fingerprint_matches = (fingerprints.get("game_timestamp") == [ROUTE_TIMESTAMP]
                           and fingerprints.get("game_image_size") == [ROUTE_IMAGE_SIZE])
    return {
        "schema": 1, "session": summary["id"], "selectedTarget": summary["selectedTarget"],
        "restored": summary.get("restored"), "outcomes": summary["outcomes"],
        "scope": summary.get("scope"), "mappings": protocol["mappings"],
        "faults": faults, "models": model_rows, "samples": samples,
        "deployment": {"files": len(inventory["files"]), "unitIds": len(deployed),
                       "resourceInstantiationProven": inventory.get("resourceInstantiationProven", False)},
        "consumerEvidence": {
            "module": "game.dll", "functionRva": "0x81f470", "bankSelectionRva": "0x8201f5",
            "captureSha256": ROUTE_SHA256, "localCaptureVerified": verified,
            "reportRuntimeFingerprintMatches": fingerprint_matches,
            "limits": "Runtime fingerprint is game.dll, not the faulting EXE; no remote byte identity or stack proof.",
        },
        "limits": [
            "Outcome o means OPEN crash, c means CLOSE crash; event timing alone does not settle this.",
            "All mappings were disabled together; this does not isolate the selected target.",
            "Bank aliases predict overwritten handle records in this captured path, not necessarily a crash.",
            "Protocol pieces are not captured live clones. Catalog body enums differ from protocol body values.",
            "Deployed Unit IDs do not prove resource instantiation. No exception stack or register context.",
        ],
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    parser.add_argument("--route-window", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = review(args.report, args.route_window)
    text = json.dumps(result, ensure_ascii=False, indent=2)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        # Preserve existing analyses rather than silently overwrite evidence.
        with args.output.open("x", encoding="utf-8") as stream:
            stream.write(text + "\n")
    else:
        print(text)
