"""Offline structural comparison of a one-shot runtime Kit snapshot."""
import argparse
from collections import Counter, defaultdict
import json
from pathlib import Path


def dword(data, offset):
    return int.from_bytes(data[offset:offset + 4], "little")


def decode(row):
    header = bytes.fromhex(row["header"])
    bodies = []
    for entry in row["rows"].split(";"):
        body_hex, piece_hex = entry.split(":", 1)
        body_header, payload = bytes.fromhex(body_hex), bytes.fromhex(piece_hex)
        count = int.from_bytes(body_header[16:24], "little")
        assert len(body_header) == 24 and len(payload) == count * 96
        pieces = [payload[i:i + 96] for i in range(0, len(payload), 96)]
        bodies.append({"kind": dword(body_header, 0), "header": body_header,
                       "pieces": pieces})
    assert len(header) == 64 and len(bodies) == row["bodies"]
    assert sum(len(body["pieces"]) for body in bodies) == row["pieces"]
    return {"header": header, "bodies": bodies}


def piece_info(data, body):
    return {"body": body, "slot": dword(data, 8), "pieceType": dword(data, 12),
            "weight": dword(data, 16), "unit": data[:8][::-1].hex(),
            "tone": data[88], "auxHex": (data[20:88] + data[89:]).hex()}


def aux_summary(pieces):
    by_position = defaultdict(lambda: [0, 0])
    for piece in pieces:
        key = f"{piece['body']}:{piece['slot']}:{piece['pieceType']}"
        by_position[key][0] += 1
        by_position[key][1] += not any(bytes.fromhex(piece["auxHex"]))
    return {"pieces": len(pieces),
            "zeroAux": sum(count[1] for count in by_position.values()),
            "byPosition": {key: {"pieces": count[0], "zeroAux": count[1]}
                           for key, count in sorted(by_position.items())}}


def effective_issues(decoded):
    issues = []
    for variant in (0, 1):
        slots = defaultdict(list)
        for body in decoded["bodies"]:
            if body["kind"] not in (variant, 3):
                continue
            for piece in body["pieces"]:
                entry = piece_info(piece, body["kind"])
                slot, kind = entry["slot"], entry["pieceType"]
                if slot >= 10:
                    issues.append({"variant": variant, "kind": "slot_outside_known_0_9",
                                   "pieces": [entry]})
                # The captured native route has A/B/C handle banks; types >= 2 alias C.
                if 1 <= slot <= 9:
                    bank = 0 if kind == 0 else 1 if kind == 1 else 2
                    slots[(slot, bank)].append(entry)
        for (slot, bank), entries in sorted(slots.items()):
            if len(entries) > 1:
                issues.append({"variant": variant, "kind": "captured_bank_alias",
                               "slot": slot, "bank": bank, "pieces": entries})
    return issues


def compare(original, published):
    old = decode(original)
    new = decode(published)
    old_positions = defaultdict(list)
    for body in old["bodies"]:
        for piece in body["pieces"]:
            old_positions[(body["kind"], dword(piece, 8), dword(piece, 12))].append(piece)
    new_pieces = [piece_info(piece, body["kind"])
                  for body in new["bodies"] for piece in body["pieces"]]
    unmatched, zero_aux, changed_aux = [], [], []
    for body in new["bodies"]:
        for piece in body["pieces"]:
            info = piece_info(piece, body["kind"])
            donors = old_positions[(body["kind"], info["slot"], info["pieceType"])]
            if not donors:
                unmatched.append(info)
                if not any(piece[20:88] + piece[89:]):
                    zero_aux.append(info)
            elif all(piece[20:88] + piece[89:] != donor[20:88] + donor[89:]
                     for donor in donors):
                changed_aux.append(info)
    return {
        "item": published["item"], "index": published["index"],
        "originalBodies": original["bodies"], "publishedBodies": published["bodies"],
        "originalPieces": original["pieces"], "publishedPieces": published["pieces"],
        "headerFirst48Preserved": old["header"][:48] == new["header"][:48],
        "bodyHeaderFirst8": [
            {"body": body["kind"], "header": body["header"][:8].hex()}
            for body in new["bodies"]],
        "newPositionCount": len(unmatched),
        "newPositionBySlot": dict(Counter(str(p["slot"]) for p in unmatched)),
        "zeroAuxNonHelmetNewPositions": sum(
            not (p["body"] == 3 and p["slot"] == 1 and p["pieceType"] == 0)
            for p in zero_aux),
        "newPositionUnits": [p["unit"] for p in unmatched],
        "zeroAuxNewPositionCount": len(zero_aux),
        "zeroAuxNewPositionUnits": [p["unit"] for p in zero_aux],
        "changedAuxAtMatchingPositionCount": len(changed_aux),
        "issues": effective_issues(new),
        "pieces": new_pieces,
    }


def protocol_check(comparisons, protocol_status):
    document = json.loads(protocol_status.read_text(encoding="utf-8"))["armorRegistry"]["protocol"]
    models = {model["key"]: model for model in document["models"]}
    mappings = {row["targetItemId"]: row for row in document["mappings"]}
    mismatches, checked = [], 0
    for row in comparisons:
        mapping = mappings.get(row["item"])
        if not mapping:
            mismatches.append({"item": row["item"], "reason": "missing protocol mapping"})
            continue
        if not mapping["isModelMapping"]:
            continue
        model = models.get(mapping["modelKey"])
        if not model:
            mismatches.append({"item": row["item"], "reason": "missing model"})
            continue
        checked += 1
        expected = Counter((piece["body"], piece["slot"], piece["pieceType"], piece["unit"])
                           for piece in model["pieces"] if piece["enabled"])
        actual = Counter((piece["body"], piece["slot"], piece["pieceType"], piece["unit"])
                         for piece in row["pieces"])
        if expected != actual:
            mismatches.append({"item": row["item"], "missing": sum((expected-actual).values()),
                               "extra": sum((actual-expected).values())})
    return {"revision": document["revision"], "mappingCount": len(mappings),
            "checkedModelClones": checked,
            "modelCloneMismatches": mismatches}


def analyze(path, protocol_status=None):
    records = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
    starts = [r for r in records if r["record_type"] == "start"]
    completed = [r for r in records if r["record_type"] == "complete"]
    assert len(starts) == len(completed) == 1, "incomplete snapshot"
    by_index = defaultdict(dict)
    for record in records:
        if record["record_type"] == "kit":
            assert record["variant"] not in by_index[record["index"]]
            by_index[record["index"]][record["variant"]] = record
    assert len(by_index) == starts[0]["count"] == completed[0]["count"] == 411
    assert all("original" in pair for pair in by_index.values())
    comparisons = [compare(pair["original"], pair["published"])
                   for pair in by_index.values() if "published" in pair]
    originals_with_alias = [
        {"item": pair["original"]["item"], "issues": effective_issues(decode(pair["original"]))}
        for pair in by_index.values()
        if effective_issues(decode(pair["original"]))
    ]
    assert len(comparisons) == completed[0]["mapped"] == completed[0]["expected_mapped"]
    all_original_pieces = [piece_info(piece, body["kind"])
                           for pair in by_index.values()
                           for body in decode(pair["original"])["bodies"]
                           for piece in body["pieces"]]
    result = {
        "schema": 1, "pid": starts[0]["pid"], "revision": starts[0]["revision"],
        "originalKits": len(by_index), "publishedKits": len(comparisons),
        "originalPieceTypes": dict(Counter(p["pieceType"] for p in all_original_pieces)),
        "originalSlots": dict(Counter(p["slot"] for p in all_original_pieces)),
        "originalAux": aux_summary(all_original_pieces),
        "originalAliases": originals_with_alias,
        "publishedSummary": {
            "newPositions": sum(r["newPositionCount"] for r in comparisons),
            "zeroAuxNewPositions": sum(r["zeroAuxNewPositionCount"] for r in comparisons),
            "zeroAuxNonHelmetNewPositions":
                sum(r["zeroAuxNonHelmetNewPositions"] for r in comparisons),
            "changedAuxAtMatchingPositions":
                sum(r["changedAuxAtMatchingPositionCount"] for r in comparisons),
            "headerFirst48Changed": [r["item"] for r in comparisons
                                      if not r["headerFirst48Preserved"]],
            "bankAliases": sum(i["kind"] == "captured_bank_alias"
                               for r in comparisons for i in r["issues"]),
            "kitsWithBankAliases": [r["item"] for r in comparisons
                                    if any(i["kind"] == "captured_bank_alias" for i in r["issues"])],
        },
        "published": comparisons,
        "limits": "Bank aliases are from one captured native consumer, not proof of the crash path.",
    }
    if protocol_status:
        result["protocolCheck"] = protocol_check(comparisons, protocol_status)
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("snapshot", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--protocol-status", type=Path)
    args = parser.parse_args()
    result = analyze(args.snapshot, args.protocol_status)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in result.items()
                      if k not in ("published", "originalAliases", "originalAux")},
                     ensure_ascii=False, indent=2))
