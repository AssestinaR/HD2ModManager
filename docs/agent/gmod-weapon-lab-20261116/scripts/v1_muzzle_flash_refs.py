# LAB (V1): find particle references in ProjectileWeaponComponent records and in entity deltas.
# Inputs (produced in the lab):
#   list-all.txt      = filediver -l -i "*"  (name, 0x<name>.0x<type> per line)
#   pwc-records.txt   = lab-probe.exe output (resource hash + 616-byte record hex, stride from the game's typelib)
#   deltas.json       = entity-delta-json-dumper output
# usage: python -I v1_muzzle_flash_refs.py <list-all.txt> <pwc-records.txt> <deltas.json>
import re, sys, json, collections

list_path, rec_path, delta_path = sys.argv[1:4]
PARTICLES_TYPE = "a8193123526fad64"
names, particles = {}, set()
for line in open(list_path, encoding="utf-8"):
    m = re.match(r"(.*?), 0x([0-9a-f]{16})\.0x([0-9a-f]{16})", line)
    if not m:
        continue
    h = int(m.group(2), 16)
    names[h] = m.group(1)
    if m.group(3) == PARTICLES_TYPE:
        particles.add(h)
print("particle resources in game:", len(particles))

# 1) base component records: scan every 4-byte-aligned 8-byte value for a particle resource hash
recs = {}
for line in open(rec_path):
    if line.startswith("#"):
        continue
    k, v = line.split()
    recs[int(k, 16)] = bytes.fromhex(v)
refs, by_offset = {}, collections.Counter()
for k, rec in recs.items():
    found = [(o, int.from_bytes(rec[o:o + 8], "little")) for o in range(0, len(rec) - 7, 4)]
    found = [(o, v) for o, v in found if v in particles]
    refs[k] = found
    for o, _ in found:
        by_offset[o] += 1
print("records:", len(recs), "particle refs by offset:", sorted(by_offset.items()))

AR = 0x968211c0033dce64  # content/fac_helldivers/equipment/primary_weapons/assault_rifle/assault_rifle
for o, v in refs.get(AR, []):
    users = [names.get(k, hex(k)).split("/")[-1] for k, f in refs.items() if any(x == v for _, x in f)]
    print(f"AR @{o}: {v:016x} {names.get(v, '')} shared by {len(users)}: {users}")

# 2) entity deltas that overwrite offset 224 (muzzle_flash) of ProjectileWeaponComponentData
d = json.load(open(delta_path, encoding="utf-8"))["deltas"]
rows = []
for ent, v in d.items():
    for p in v.get("modified_components", {}).get("ProjectileWeaponComponentData", []):
        b = bytes.fromhex(p["data"])
        if p["offset"] <= 224 < p["offset"] + len(b) - 7:
            i = 224 - p["offset"]
            val = int.from_bytes(b[i:i + 8], "little")
            rows.append((ent.split("/")[-1], f"{val:016x}", val in particles))
print("deltas overriding muzzle_flash:", len(rows))
for r in sorted(rows):
    print("  ", r)
