"""Generate the self-contained runtime armor registry addon."""
import argparse
from pathlib import Path


def build(project: Path, output: Path) -> None:
    protocol = (project / "src/armor_registry_protocol.lua").read_text(encoding="utf-8")
    runtime = (project / "src/armor_registry_runtime.lua").read_text(encoding="utf-8")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        f"local ARMOR_REGISTRY_PROTOCOL=(function()\n{protocol}\nend)()\n{runtime}",
        encoding="utf-8",
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    build(Path(__file__).resolve().parent.parent, args.output)
