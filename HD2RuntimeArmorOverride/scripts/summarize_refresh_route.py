"""Validate captured route-code windows offline, without accessing a process.

Instruction boundaries are checked from captured unwind-root windows.
Windows can include neighboring functions; this is NOT an ABI validator.
"""
import argparse
import hashlib
import json
from pathlib import Path

import capstone


def summarize(directory: Path):
    rows = []
    for file in sorted(directory.glob("refresh-*.jsonl")):
        rows.extend(json.loads(line) for line in file.read_text(encoding="utf-8").splitlines())
    rows = [r for r in rows if r.get("phase") == "preflight_route_code"]
    identities = {(r["session"], r["request"]) for r in rows}
    if len(identities) != 1:
        raise ValueError("Expected one route capture")
    ends = [r for r in rows if r["kind"] == "code_survey_end"]
    if len(ends) != 1 or not ends[0].get("complete"):
        raise ValueError("Incomplete route survey")
    if any(r["kind"] == "code_survey_error" for r in rows):
        raise ValueError("Survey contains errors")
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    decoded, windows = {}, []
    for window in (r for r in rows if r["kind"] == "route_window"):
        root, size = int(window["root_rva"]), int(window["byte_count"])
        chunks = sorted(
            (r for r in rows if r["kind"] == "route_code_chunk" and r["root_rva"] == root),
            key=lambda r: r["rva"],
        )
        data = bytearray()
        for chunk in chunks:
            if int(chunk["rva"]) != root + len(data):
                raise ValueError("Gap or overlapping code chunks")
            data.extend(bytes.fromhex(chunk["code_hex"]))
        if len(data) != size:
            raise ValueError("Window length mismatch")
        instructions = list(md.disasm(bytes(data), root))
        own = {i.address: i for i in instructions}
        decoded.update(own)
        filename = f"route-{root:x}.asm.txt"
        (directory / filename).write_text(
            "; Bounded unwind-root window, may include neighboring functions.\n"
            + "\n".join(f"{i.address:08x}: {i.mnemonic} {i.op_str}" for i in instructions)
            + "\n", encoding="utf-8")
        (directory / f"route-{root:x}.bin").write_bytes(data)
        windows.append({"root_rva": hex(root), "bytes": size,
                        "sha256": hashlib.sha256(data).hexdigest(),
                        "decoded_until": hex(instructions[-1].address + instructions[-1].size)
                        if instructions else hex(root), "file": filename})
    candidates = []
    for row in (r for r in rows if r["kind"] == "route_envelope"):
        site = int(row["site_rva"])
        ins = decoded.get(site)
        if row["state"] != "captured" or ins is None:
            raise ValueError(f"Unverified candidate {site:x}")
        candidates.append({"label": row["label"], "site_rva": hex(site),
                           "root_rva": hex(int(row["root_rva"])),
                           "instruction": f"{ins.mnemonic} {ins.op_str}",
                           "boundary_verified": True})

    # Current evidence only: assert actual instructions, not old-to-new delta.
    checks = {
        0x874BFC: ("cmp", "byte ptr [r15 + 0x29], r14b"),
        0x874C0D: ("call", "0x877ab0"),
        0x874C12: ("mov", "byte ptr [r15 + 0x29], r14b"),
        0x874573: ("shl", "rax, 6"),
        0x87457A: ("imul", "rax, rcx, 0x44"),
        0x877CC5: ("call", "0x81e7a0"),
        0x877DD2: ("call", "0x81f470"),
        0x877F25: ("call", "0x81ed60"),
        0x81E964: ("mov", "r14d, dword ptr [rax + 0x38]"),
        0x81E976: ("mov", "r10, qword ptr [rbp + 0x30]"),
        0x81E9AB: ("shl", "rcx, 5"),
    }
    for site, expected in checks.items():
        i = decoded.get(site)
        if i is None or (i.mnemonic, i.op_str) != expected:
            raise ValueError(f"Structural assertion failed at {site:x}")
    globals_found = []
    for site, expected in [(0x874F8F, 0x33264F8), (0x81E913, 0x33264F8),
                           (0x877B79, 0x3326D20), (0x874B4B, 0x3326468)]:
        i = decoded[site]
        op = i.operands[1]
        if op.type != capstone.x86.X86_OP_MEM or op.mem.base != capstone.x86.X86_REG_RIP:
            raise ValueError("Expected RIP-relative global")
        target = i.address + i.size + op.mem.disp
        if target != expected:
            raise ValueError("Global target mismatch")
        globals_found.append({"site_rva": hex(site), "global_rva": hex(target)})
    result = {
        "session_request": list(next(iter(identities))),
        "survey": ends[0], "windows": windows, "candidates": candidates,
        "structural_assertions_passed": len(checks), "globals": globals_found,
        "local_player_identity_validated": False, "pure_visual_entry_validated": False,
        "native_calls_executed": False,
        "warning": "Static current-build chain, not an executed equip trace or callable ABI.",
    }
    (directory / "route-analysis.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    result = summarize(args.directory)
    print(json.dumps({"candidates": len(result["candidates"]),
                      "structural_assertions": result["structural_assertions_passed"],
                      "native_calls_executed": False}))
