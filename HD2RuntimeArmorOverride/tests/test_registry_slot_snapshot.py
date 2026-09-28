import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


MODULE = Path(__file__).resolve().parents[1] / "scripts" / "analyze_registry_slots.py"
spec = importlib.util.spec_from_file_location("analyze_registry_slots", MODULE)
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


def piece(unit, slot, kind, aux=b""):
    data = bytearray(96)
    data[:8] = unit.to_bytes(8, "little")
    data[8:12] = slot.to_bytes(4, "little")
    data[12:16] = kind.to_bytes(4, "little")
    data[24:24 + len(aux)] = aux
    return bytes(data)


def record(pieces):
    header = bytearray(64)
    body = bytearray(24)
    body[16:24] = len(pieces).to_bytes(8, "little")
    return {"header": header.hex(), "rows": body.hex() + ":" + b"".join(pieces).hex(),
            "bodies": 1, "pieces": len(pieces), "item": 42, "index": 0}


class SnapshotAnalysisTests(unittest.TestCase):
    def test_new_position_zeros_are_distinct_from_preserved_aux(self):
        original = record([piece(1, 2, 0, b"hash")])
        published = record([piece(2, 2, 0, b"hash"), piece(3, 3, 0)])
        result = audit.compare(original, published)
        self.assertEqual(result["newPositionCount"], 1)
        self.assertEqual(result["zeroAuxNewPositionCount"], 1)
        self.assertEqual(result["zeroAuxNonHelmetNewPositions"], 1)
        self.assertEqual(result["changedAuxAtMatchingPositionCount"], 0)
        self.assertEqual(result["issues"], [])

    def test_same_slot_type_two_and_three_alias_output_bank(self):
        decoded = audit.decode(record([piece(1, 2, 2), piece(2, 2, 3)]))
        self.assertEqual(len(audit.effective_issues(decoded)), 1)
        self.assertTrue(all(issue["bank"] == 2 for issue in audit.effective_issues(decoded)))

    def test_aux_stats_are_position_scoped(self):
        info = [audit.piece_info(piece(1, 1, 0), 3),
                audit.piece_info(piece(2, 2, 0, b"hash"), 0)]
        result = audit.aux_summary(info)
        self.assertEqual((result["pieces"], result["zeroAux"]), (2, 1))
        self.assertEqual(result["byPosition"]["3:1:0"]["zeroAux"], 1)
        self.assertEqual(result["byPosition"]["0:2:0"]["zeroAux"], 0)

    def test_protocol_comparison_checks_all_fields_not_just_unit(self):
        document = {"armorRegistry": {"protocol": {
            "revision": 7, "mappings": [{"targetItemId": 42, "isModelMapping": True,
                                          "modelKey": "model"}],
            "models": [{"key": "model", "pieces": [
                {"body": 0, "slot": 2, "pieceType": 0, "unit": "0000000000000001",
                 "enabled": True}]}]}}}
        comparison = {"item": 42, "pieces": [
            audit.piece_info(piece(1, 2, 0), 0)]}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "status.json"
            path.write_text(json.dumps(document), encoding="utf-8")
            self.assertEqual(audit.protocol_check([comparison], path)["modelCloneMismatches"], [])
            comparison["pieces"][0]["pieceType"] = 1
            result = audit.protocol_check([comparison], path)
            self.assertEqual(result["modelCloneMismatches"][0]["extra"], 1)
            self.assertEqual(result["modelCloneMismatches"][0]["missing"], 1)


if __name__ == "__main__":
    unittest.main()
