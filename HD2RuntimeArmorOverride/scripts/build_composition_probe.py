"""Generate a self-contained read-only probe from reviewed sources and samples."""
import argparse
import json
from pathlib import Path


def build(project, output):
    samples = json.loads((project / "evidence/composition-samples.json").read_text(encoding="utf-8-sig"))
    rows = []
    for kit in samples["kits"]:
        signatures = []
        for p in kit["pieces"]:
            resources = ",".join(f"{k}={v}" for k, v in sorted(p["resources"].items()))
            signatures.append("|".join(str(v) for v in (
                p["rawBodyType"], p["slot"], p["pieceType"], p["weight"],
                p["toneVariations"], p["unit"], resources)))
        signature = "\n".join(sorted(signatures))
        body_count = len({p["rawBodyType"] for p in kit["pieces"]})
        rows.append(f'[{kit["itemId"]}]={{archive="{kit["archive"]}",'
                    f'bodyCount={body_count},pieceCount={len(signatures)},'
                    f'signature={json.dumps(signature)}}}')
    schema = (project / "src/composition_schema.lua").read_text(encoding="utf-8")
    chain = (project / "src/composition_chain.lua").read_text(encoding="utf-8")
    index = (project / "src/composition_index.lua").read_text(encoding="utf-8")
    probe = (project / "src/composition_runtime_probe.lua").read_text(encoding="utf-8")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        f"local COMPOSITION_SCHEMA=(function()\n{schema}\nend)()\n"
        f"local COMPOSITION_INDEX=(function()\n{index}\nend)()\n"
        f"local COMPOSITION_EXPECTED={{{','.join(rows)}}}\n"
        f"local COMPOSITION_VERIFY=(function()\n{chain}\nend)()"
        "(COMPOSITION_SCHEMA,COMPOSITION_EXPECTED)\n" + probe,
        encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    build(Path(__file__).resolve().parent.parent, args.output)
