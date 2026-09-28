"""Offline report-analysis tests; no game, deployment or private fixture needed."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location(
    "armory_report", Path(__file__).resolve().parents[1] / "scripts/review_armory_report.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def piece(body, kind, unit, slot=2, enabled=True):
    return dict(body=body, pieceType=kind, unit=unit, slot=slot, enabled=enabled)


class ArmoryReportTests(unittest.TestCase):
    def test_distinct_types_can_alias_same_output_bank(self):
        result = module.composition([piece(0, 2, "a"), piece(0, 3, "b")])
        self.assertEqual(result["0"]["duplicatePositions"], [])
        self.assertEqual(len(result["0"]["candidateOutputBankAliases"]), 1)
        self.assertEqual(result["1"]["effectivePieces"], 0)

    def test_bodies_do_not_collide_but_any_can(self):
        rows = [piece(0, 2, "a"), piece(1, 2, "b"), piece(3, 3, "c")]
        result = module.composition(rows)
        for body in ("0", "1"):
            self.assertEqual(result[body]["effectivePieces"], 2)
            self.assertEqual(len(result[body]["candidateOutputBankAliases"]), 1)

    def test_three_banks_disabled_and_helmet_exclusions(self):
        rows = [piece(3, i, str(i)) for i in range(3)]
        rows += [piece(3, 3, "disabled", enabled=False), piece(3, 3, "helmet", slot=0)]
        result = module.composition(rows)["0"]
        self.assertEqual(result["candidateOutputBankAliases"], [])
        self.assertEqual(result["outsideCapturedBodySlotRange"], ["helmet"])

    def test_stale_and_unavailable_samples_not_counted_as_ready(self):
        process = dict(pid=7, startUtc="2026-09-26T06:00:00Z",
                       privateMemoryBytes=20, workingSetBytes=10)
        rows = [dict(state="sample_unavailable"),
                dict(utc="a", processes=[], runtime={"pid": "7", "state": "ready"}),
                dict(utc="b", processes=[process], runtime={"pid": "8", "state": "ready"}),
                dict(utc="c", processes=[process], runtime={"pid": "7", "state": "ready"})]
        result = module.summarize_samples(rows)
        self.assertEqual(result["matchingPidStatusRows"], 1)
        self.assertEqual(result["unmatchedOrStaleStatusRows"], 2)
        self.assertEqual(result["unavailableRows"], 1)
        self.assertEqual(result["processes"][0]["peakPrivateBytes"], 20)


if __name__ == "__main__":
    unittest.main()
