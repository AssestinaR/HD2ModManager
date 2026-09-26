"""Offline regression checks; never attach to a process."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location(
    "code_refs", Path(__file__).resolve().parents[1] / "scripts/summarize_refresh_code_refs.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class CodeRefsTests(unittest.TestCase):
    def test_raw_window_never_asserts_boundary(self):
        rows = [
            dict(kind="focused_code_window", module="game.dll", rva=0x1000,
                 code_hex="c3", state="captured", called=False, boundary_verified=False),
            dict(kind="code_survey_end"),
        ]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            (path / "refresh-test.jsonl").write_text("\n".join(
                json.dumps(dict(row, session="fixture", request=3)) for row in rows),
                encoding="utf-8")
            result = module.summarize(path)
            window = result["diagnostic_snapshots"][0]
            self.assertFalse(window["boundary_verified"])
            self.assertIn("does NOT verify", (path / window["disassembly"]).read_text())
        self.assertFalse(result["refresh_entry_found"])

    def test_rex_shared_capture_and_root(self):
        rows = [
            dict(kind="api_root_snapshot", identity="helldivers2.exe+27cab00", path="root+e0"),
            dict(kind="table_call_candidate", module="game.dll", function_begin=0x1000,
                 site_rva=0x1001, displacement=0x108, envelope_state="captured",
                 code_hex="41ff9008010000ff9008010000c3"),
            dict(kind="table_call_candidate", module="game.dll", function_begin=0x1000,
                 site_rva=0x1007, displacement=0x108, envelope_state="shared_capture"),
            dict(kind="code_survey_end"),
        ]
        for row in rows:
            row.update(session="fixture", request=3)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            (path / "refresh-test.jsonl").write_text(
                "\n".join(json.dumps(row) for row in rows), encoding="utf-8")
            result = module.summarize(path)
        self.assertTrue(result["complete"])
        self.assertEqual(len(result["api_roots"]), 1)
        self.assertEqual(result["references"][0]["instruction_start_rva"], 0x1000)
        for row in result["references"]:
            self.assertTrue(row["instruction_boundary_verified"])
            self.assertTrue(row["displacement_verified"])
            self.assertFalse(row["callable_entry_proven"])
        self.assertFalse(result["refresh_entry_found"])


if __name__ == "__main__":
    unittest.main()
