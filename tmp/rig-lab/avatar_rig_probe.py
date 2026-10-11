"""A1: derive the HD2 Helldiver rig from the GAME ITSELF, and check it.

Reads the avatar Unit TocData (extracted with `hd2mod gamedata extract asset`),
parses the TransformInfo table exactly as HD2ModAdaptation does, and compares
the resulting bone positions against the reference constant hd2_skel.json.

Acceptance: max per-bone error < 1 mm.  If this passes, we no longer have to
trust any shared constant -- we can derive the rig ourselves.

Table layout (mirrors UnitMeshReader.ReadTransformInfo):
    u32 @ 0x34              transformInfoOffset
    u32 @ tio               count
    u32 @ tio+4, +8, +12    reserved
    tio+16  + i*64          local:  9 floats rot | 3 floats pos | 3 floats scale | 1 float pad
    + count*64              matrix: 16 floats
    + count*64              entry:  u16 increment | u16 parentIndex
    + count*4               name hash: u32
"""
import json, math, struct, sys

TOC = sys.argv[1]
SKEL = sys.argv[2]
HASHES = sys.argv[3] if len(sys.argv) > 3 else None

data = open(TOC, 'rb').read()
print('toc size = %d bytes' % len(data))

tio = struct.unpack_from('<I', data, 0x34)[0]
print('transformInfoOffset = 0x%x' % tio)
if tio == 0 or tio >= len(data):
    print('!! unsupported / absent transform info'); sys.exit(1)

count = struct.unpack_from('<I', data, tio)[0]
res = struct.unpack_from('<III', data, tio + 4)
print('count = %d   reserved = %s' % (count, res))

local_off = tio + 16
mat_off = local_off + count * 64
ent_off = mat_off + count * 64
hash_off = ent_off + count * 4
print('locals@0x%x  matrices@0x%x  entries@0x%x  hashes@0x%x  end=0x%x (size %d)'
      % (local_off, mat_off, ent_off, hash_off, hash_off + count * 4, len(data)))

locals_, matrices, entries, hashes = [], [], [], []
for i in range(count):
    lo = local_off + i * 64
    rot = struct.unpack_from('<9f', data, lo)
    pos = struct.unpack_from('<3f', data, lo + 36)
    scl = struct.unpack_from('<3f', data, lo + 48)
    locals_.append(dict(rot=rot, pos=pos, scale=scl))
    matrices.append(struct.unpack_from('<16f', data, mat_off + i * 64))
    inc, par = struct.unpack_from('<HH', data, ent_off + i * 4)
    entries.append((inc, par))
    hashes.append(struct.unpack_from('<I', data, hash_off + i * 4)[0])

# name lookup
names = {}
if HASHES:
    for line in open(HASHES, encoding='utf-8', errors='replace'):
        parts = line.split()
        if len(parts) >= 2:
            try:
                names[int(parts[0])] = parts[1]
            except ValueError:
                pass

named = sum(1 for h in hashes if h in names)
print('hashes resolved to a name: %d / %d' % (named, count))

# what do the 16 floats mean?  try the two conventions and see which looks like a position
print('\n-- matrix translation candidates (first 6 bones) --')
print('%-4s %-28s %-30s %-30s' % ('i', 'name', 'row-major m[12..14]', 'col-major m[3],m[7],m[11]'))
for i in range(min(6, count)):
    nm = names.get(hashes[i], '0x%08x' % hashes[i])
    m = matrices[i]
    print('%-4d %-28s (%8.4f %8.4f %8.4f)  (%8.4f %8.4f %8.4f)'
          % (i, nm, m[12], m[13], m[14], m[3], m[7], m[11]))

# compare against hd2_skel.json
skel = {k: v for k, v in json.load(open(SKEL, encoding='utf-8')).items()
        if isinstance(v, list) and len(v) == 3}

for label, pick in (('row-major m[12..14]', lambda m: (m[12], m[13], m[14])),
                    ('col-major m[3],m[7],m[11]', lambda m: (m[3], m[7], m[11])),
                    ('local pos', lambda m: None)):
    errs = []
    for i in range(count):
        nm = names.get(hashes[i])
        if not nm or nm not in skel:
            continue
        p = locals_[i]['pos'] if label == 'local pos' else pick(matrices[i])
        ref = skel[nm]
        errs.append((nm, math.dist(p, ref)))
    if not errs:
        print('\n[%s] no comparable bones' % label); continue
    errs.sort(key=lambda t: -t[1])
    mx = errs[0][1]; mean = sum(e for _, e in errs) / len(errs)
    print('\n[%s] compared %d bones   max=%.6f m   mean=%.6f m' % (label, len(errs), mx, mean))
    for nm, e in errs[:5]:
        print('    worst: %-28s %.6f m' % (nm, e))
