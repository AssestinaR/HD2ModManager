"""Offline disassembly of bounded code-reference candidates. Requires capstone."""
import argparse
import json
from pathlib import Path
import capstone


def summarize(directory: Path) -> dict:
    cs = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    cs.detail = True
    refs, pointers, phases, ends, errors, roots = [], [], set(), [], [], []
    diagnostics = []
    envelopes = {}
    for path in sorted(directory.glob("refresh-*.jsonl")):
        for line in path.read_text(encoding="utf-8").splitlines():
            row = json.loads(line)
            if row.get("kind") not in ("code_reference_candidate", "pointer_access_candidate", "table_call_candidate",
                                      "module_pointer_candidate", "code_survey_end", "code_survey_error",
                                      "api_root_snapshot", "code_seed_range", "code_seed_status",
                                      "dispatch_table_snapshot", "component_list_snapshot",
                                      "focused_code_window", "rtti_candidate"):
                continue
            phases.add((row["session"], row["request"]))
            if row["kind"] == "focused_code_window":
                snapshot = {k: v for k, v in row.items() if k != "code_hex"}
                if row.get("code_hex"):
                    instructions = list(cs.disasm(bytes.fromhex(row["code_hex"]), row["rva"]))
                    name = f"window-{row['module']}-{row['rva']:x}.asm.txt"
                    (directory / name).write_text(
                        "; Arbitrary window start: decoding alone does NOT verify boundaries.\n"
                        + "\n".join(f"{i.address:08x}: {i.mnemonic} {i.op_str}" for i in instructions)
                        + "\n", encoding="utf-8")
                    snapshot["disassembly"] = name
                diagnostics.append(snapshot)
                continue
            if row["kind"] in ("dispatch_table_snapshot", "component_list_snapshot", "code_seed_status",
                               "rtti_candidate"):
                diagnostics.append(row)
                continue
            if row["kind"] == "api_root_snapshot":
                roots.append(row)
                continue
            if row["kind"] == "module_pointer_candidate":
                pointers.append(row)
                continue
            if row["kind"] == "code_survey_end":
                ends.append(row)
                continue
            if row["kind"] == "code_survey_error":
                errors.append(row)
                continue
            result = {k: v for k, v in row.items() if k != "code_hex"}
            result["instruction_boundary_verified"] = False
            key = (row["session"], row["request"], row.get("module"), row.get("function_begin"))
            if row.get("code_hex"):
                envelopes[key] = bytes.fromhex(row["code_hex"])
            data = envelopes.get(key)
            if data is not None and row.get("envelope_state") in ("captured", "shared_capture"):
                instructions = list(cs.disasm(data, row["function_begin"]))
                site = next((i for i in instructions if i.address == row["site_rva"]), None)
                if site is None and row["kind"] == "table_call_candidate":
                    # Survey records FF opcode position; an optional REX prefix
                    # belongs to the decoded instruction, not a separate opcode.
                    site = next((i for i in instructions
                                 if i.address + 1 == row["site_rva"]
                                 and len(i.bytes) >= 2 and 0x40 <= i.bytes[0] <= 0x4f
                                 and i.bytes[1] == 0xff), None)
                    if site is not None:
                        result["instruction_start_rva"] = site.address
                result["envelope_fully_decoded"] = sum(i.size for i in instructions) == len(data)
                if row["kind"] == "table_call_candidate":
                    result["instruction_boundary_verified"] = site is not None and site.mnemonic in ("call", "jmp")
                    result["displacement_verified"] = bool(site and any(
                        op.type == capstone.x86.X86_OP_MEM and op.mem.disp == row["displacement"]
                        for op in site.operands))
                elif row["kind"] == "pointer_access_candidate":
                    result["instruction_boundary_verified"] = site is not None and site.mnemonic in ("mov", "lea")
                    result["destination_verified"] = False
                    if site:
                        result["destination_verified"] = any(
                            op.type == capstone.x86.X86_OP_MEM
                            and op.mem.base == capstone.x86.X86_REG_RIP
                            and site.address + site.size + op.mem.disp == row["destination_rva"]
                            for op in site.operands
                        )
                elif row["kind"] != "code_seed_range":
                    result["instruction_boundary_verified"] = (
                        site is not None and site.mnemonic in ("call", "jmp")
                        and site.bytes[0] == row["opcode"]
                    )
                if site:
                    result["instruction"] = f"{site.mnemonic} {site.op_str}"
                    if row.get("reference_kind") == "rip_indirect":
                        op = site.operands[0]
                        result["pointer_slot_verified"] = (
                            op.type == capstone.x86.X86_OP_MEM
                            and op.mem.base == capstone.x86.X86_REG_RIP
                            and site.address + site.size + op.mem.disp == row["pointer_slot_rva"]
                        )
                # Exception directory entries may describe chained ranges /
                # funclets, not a callable top-level function entry.
                result["callable_entry_proven"] = False
                text = "\n".join(f"{i.address:08x}: {i.mnemonic} {i.op_str}" for i in instructions)
                name = f"{row['module']}-{row['site_rva']:x}.asm.txt"
                (directory / name).write_text(text + "\n", encoding="utf-8")
                result["disassembly"] = name
            refs.append(result)
    if len(phases) != 1:
        raise ValueError("Expected a single code-survey session/phase")
    return {
        "schema": 1, "complete": len(ends) == 1 and not errors,
        "survey": ends, "errors": errors, "references": refs, "module_pointers": pointers,
        "api_roots": roots,
        "diagnostic_snapshots": diagnostics,
        "refresh_entry_found": False,
        "note": "Static edges only; no observation that a function ran during equip. "
                "Exception ranges are not necessarily callable entries. "
                "RIP-indirect pointers reflect the captured instant. Register-displacement call candidates "
                "are bounded and may be prefiltered; decoded offsets alone do not prove API provenance.",
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    args = parser.parse_args()
    result = summarize(args.capture)
    (args.capture / "code-reference-summary.json").write_text(
        json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "complete": result["complete"],
        "module_pointers": [
            {k: r.get(k) for k in ("module", "slot_rva", "target", "neighbours")}
            for r in result["module_pointers"]
        ],
        "references": [
            {k: r.get(k) for k in ("module", "site_rva", "target", "reference_kind",
                                  "instruction_boundary_verified", "pointer_slot_verified", "destination_verified")}
            for r in result["references"]
        ],
    }, indent=2))
