"""Summarise a `unitprobe` dump: one row per Unit entry.

Reads the UTF-16 text that PowerShell redirection produces, groups the
per-unit header line with the first mesh's semantic line, and prints a table
plus the total GPU payload. Read-only.
"""

import re
import sys

PATH = sys.argv[1] if len(sys.argv) > 1 else r"E:\tmp\hd2-mem\luna-units.txt"

raw = open(PATH, "rb").read()
text = None
for encoding in ("utf-16", "utf-8-sig", "utf-8"):
    try:
        text = raw.decode(encoding)
        break
    except UnicodeDecodeError:
        continue
if text is None:
    raise SystemExit("cannot decode dump")

lines = [line.strip() for line in text.replace("\r", "").split("\n")]

units = []
current = None
for line in lines:
    header = re.match(r"UNIT (\d+) fileId=0x([0-9a-f]+)", line)
    if header:
        current = {"index": int(header.group(1)), "file_id": header.group(2)}
        units.append(current)
        continue
    if current is None:
        continue
    payload = re.match(r"toc=(\d+) gpu=(\d+) stream=(\d+) ver=0x([0-9a-f]+) meshes=(\d+) streams=(\d+) materials=(\d+)", line)
    if payload:
        current.update(
            gpu=int(payload.group(2)),
            meshes=int(payload.group(5)),
            materials=int(payload.group(7)),
        )
        continue
    semantic = re.search(r"sem=\[slot=(\S+) type=(\S+) body=(\S+) weight=(\S+)", line)
    if semantic and "slot" not in current:
        current["slot"] = semantic.group(1)
        current["type"] = semantic.group(2)
        current["body"] = semantic.group(3)

print("%-4s %-18s %10s %7s %6s %-14s %-13s %-7s" % (
    "idx", "fileId", "gpu", "meshes", "mats", "slot", "type", "body"))
total = 0
for unit in units:
    total += unit.get("gpu", 0)
    print("%-4d %-18s %10d %7d %6d %-14s %-13s %-7s" % (
        unit["index"], unit["file_id"], unit.get("gpu", 0), unit.get("meshes", 0),
        unit.get("materials", 0), unit.get("slot", "?"), unit.get("type", "?"),
        unit.get("body", "?")))
print("units=%d total_gpu=%.1f MB" % (len(units), total / 1048576))
