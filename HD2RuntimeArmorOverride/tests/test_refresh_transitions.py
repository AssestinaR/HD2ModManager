import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location(
    "transitions", Path(__file__).resolve().parents[1] / "scripts/summarize_refresh_transitions.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class TransitionsTests(unittest.TestCase):
    def test_changes_failures_and_session_guard(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            common = dict(session="a", request=2, phase="equip_b")
            start = dict(common, kind="phase_begin", mode="owner_trace", pid=123, registry_revision=45)

            def observation(t, children, **extra):
                return dict(common, kind="native_owner_trace", seconds=t, sample_index=t,
                            state="resolved_candidate", association_state="complete",
                            object="0x10000", object_world="0x20000", handle_index=1,
                            handle_generation=2, association_count=len(children),
                            association_objects=";".join(children), **extra)

            rows = [start, observation(1, ["a", "b"]), observation(2, ["b", "a"]),
                    observation(3, ["b", "c"]),
                    dict(common, kind="native_owner_trace", seconds=4, state="generation_mismatch"),
                    observation(5, ["d"]), dict(common, kind="phase_end", truncated=False)]
            path = directory / "refresh-a-2.jsonl"

            def save():
                path.write_text("\n".join(json.dumps(r) for r in rows), encoding="utf-8")

            save()
            phase = module.summarize(directory)["phases"][0]
            self.assertTrue(phase["complete_log"])
            self.assertTrue(phase["observations"][1]["order_changed"])
            self.assertFalse(phase["observations"][1]["membership_changed"])
            self.assertEqual(["c"], phase["observations"][2]["links_added"])
            self.assertEqual(["a"], phase["observations"][2]["links_removed"])
            self.assertEqual("first_complete_observation", phase["observations"][4]["comparison"])
            rows.pop()
            save()
            self.assertFalse(module.summarize(directory)["phases"][0]["complete_log"])
            rows[2]["association_count"] = 3
            save()
            with self.assertRaisesRegex(ValueError, "Malformed"):
                module.summarize(directory)
            rows[2]["association_count"] = 2
            save()
            other = directory / "refresh-b-2.jsonl"
            other.write_text(json.dumps(dict(start, session="b", pid=456)), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "mixed sessions"):
                module.summarize(directory)


if __name__ == "__main__":
    unittest.main()
