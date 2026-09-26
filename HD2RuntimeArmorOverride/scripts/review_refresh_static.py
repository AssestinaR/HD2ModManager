"""Offline review of the historical mapped image. Never opens a process.

RVAs are historical evidence only, NOT current-build function addresses.
Requires capstone. Follows chained unwind records instead of treating each
RUNTIME_FUNCTION fragment as a complete function.
"""
import argparse
import bisect
import hashlib
import json
import struct
from pathlib import Path

import capstone


def review(source: Path, output: Path):
    data = source.read_bytes()

    def unpack(fmt, offset):
        if offset < 0 or offset + struct.calcsize(fmt) > len(data):
            raise ValueError("Out-of-image metadata")
        return struct.unpack_from(fmt, data, offset)

    pe, = unpack("<I", 0x3C)
    if data[:2] != b"MZ" or data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("Expected mapped PE image")
    timestamp, = unpack("<I", pe + 8)
    image_size, = unpack("<I", pe + 24 + 56)
    if (timestamp, image_size, len(data)) != (1787171630, 61255680, 61255680):
        raise ValueError("Historical-image fingerprint mismatch; do not reuse RVAs")
    directory, size = unpack("<II", pe + 24 + 112 + 3 * 8)
    if size % 12:
        raise ValueError("Invalid exception directory")
    records = sorted(unpack("<III", at) for at in range(directory, directory + size, 12))
    starts = [r[0] for r in records]

    def chain_root(record):
        seen = set()
        for _ in range(16):
            begin, end, unwind = record
            if record in seen or not 0 <= begin < end <= len(data):
                raise ValueError("Invalid unwind chain")
            seen.add(record)
            version_flags, _, codes, _ = unpack("<BBBB", unwind)
            if version_flags & 7 not in (1, 2):
                raise ValueError("Unsupported unwind version")
            if not (version_flags >> 3) & 4:
                return begin
            record = unpack("<III", unwind + 4 + ((codes + 1) & ~1) * 2)
        raise ValueError("Unwind chain limit")

    groups = {}
    rejected = 0
    for record in records:
        try:
            groups.setdefault(chain_root(record), []).append(record)
        except ValueError:
            rejected += 1
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    seeds = {
        0x869810: "armor registry filename anchor",
        0x1282419: "EquipItem property registration, not equip execution",
        0x11CEB91: "configuration comparison calls state setter",
        0x162D31E: "broader configuration apply calls state setter",
        0x86B190: "state setter, equal-value early exit",
        0x86A730: "state comparison and deferred update",
        0x86AE1A: "pending flag branch calls candidate update then clears flag",
        0x86DB90: "candidate update; includes nonvisual side effects",
        0x8157E0: "downstream assembly candidate A",
        0x8164B0: "downstream assembly candidate B",
        0x815DA0: "downstream assembly candidate C",
        0x86D2E0: "broader update also reaches Player_ArmorWeight audio switch",
        0xD3E370: "typed queue submission, not established resource resolver",
        0x11C4892: "UI-side pending marker consumer, not established model flag",
    }
    output.mkdir(parents=True, exist_ok=True)
    results = []
    for site, label in seeds.items():
        index = bisect.bisect_right(starts, site) - 1
        if index < 0 or not records[index][0] <= site < records[index][1]:
            raise ValueError(f"Missing site {site:x}")
        root = chain_root(records[index])
        instructions, text, segments = {}, [], groups[root]
        for begin, end, unwind in segments:
            text.append(f"; segment {begin:x}-{end:x}; unwind {unwind:x}; chain root {root:x}")
            decoded = list(md.disasm(data[begin:end], begin))
            if not decoded or decoded[-1].address + decoded[-1].size != end:
                raise ValueError(f"Incomplete decode of segment {begin:x}")
            for ins in decoded:
                instructions[ins.address] = ins
                text.append(f"{ins.address:08x}: {ins.mnemonic} {ins.op_str}")
        if site not in instructions:
            raise ValueError(f"Site not on instruction boundary: {site:x}")
        filename = f"old-chain-{root:x}.asm.txt"
        (output / filename).write_text("\n".join(text) + "\n", encoding="utf-8")
        ins = instructions[site]
        results.append({
            "label": label, "site_rva": hex(site), "chain_root_rva": hex(root),
            "instruction": f"{ins.mnemonic} {ins.op_str}", "disassembly": filename,
            "segments": [[hex(v) for v in r] for r in segments],
            "site_boundary_verified": True, "current_build_validated": False,
            "safe_to_call": False,
        })
    result = {
        "source": str(source.resolve()), "sha256": hashlib.sha256(data).hexdigest(),
        "historical_timestamp": timestamp, "historical_image_size": image_size,
        "current_observed_timestamp": 1790161983, "current_observed_image_size": 74727424,
        "same_build": False, "rejected_unwind_records": rejected,
        "candidates": results,
        "limit": "Static historical evidence only; no execution, ABI or visual-only safety proof.",
    }
    (output / "static-review.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps({"candidates": len(results), "same_build": False,
                      "rejected_unwind_records": rejected, "output": str(output.resolve())}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    review(args.source, args.output)
