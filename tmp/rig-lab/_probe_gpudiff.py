# -*- coding: utf-8 -*-
"""Byte-range diff between two GPU sidecars: which entries changed, and is it
ONLY the albedo pixel blocks?

usage: _probe_gpudiff.py <gpuA> <gpuB> [patch_0]
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_asset as A
import hd2_patch as HP

if len(sys.argv) < 3:
    print(__doc__); sys.exit(1)

ga_path, gb_path = sys.argv[1], sys.argv[2]
a = open(ga_path, 'rb').read()
b = open(gb_path, 'rb').read()
print('gpu A %d B   gpu B %d B' % (len(a), len(b)))
if len(a) != len(b):
    print('!! sizes differ'); sys.exit(1)

# find contiguous differing runs
runs = []
i = 0
n = len(a)
while i < n:
    if a[i] != b[i]:
        j = i
        while j < n and a[j] != b[j]:
            j += 1
        runs.append((i, j))
        i = j
    else:
        i += 1

total = sum(e - s for s, e in runs)
print('differing bytes: %d of %d (%.3f%%) in %d runs'
      % (total, n, 100.0 * total / n, len(runs)))

# attribute each run to an entry
patch = sys.argv[3] if len(sys.argv) > 3 else \
    r'E:/mod/Lilium-Sancta-RE2310/9ba626afa44a3aa3.patch_0'
entries, _raw = HP.scan_entries(patch)
TEX = A.TEXTURE_TYPE_ID

# which tex ids are albedo
albedo = set()
for e in entries:
    if e['type_id'] != A.MATERIAL_TYPE_ID:
        continue
    m = A.Material.read(HP.entry_payload(e, patch)['toc'])
    k = 5 if len(m.tex_ids) == 11 else (2 if len(m.tex_ids) == 3 else None)
    if k is not None:
        albedo.add(m.tex_ids[k])

buckets = {}
outside = 0
for s, e in runs:
    hit = None
    for ent in entries:
        if ent['type_id'] != TEX:
            continue
        if ent['gpu_offset'] is not None and ent['gpu_offset'] <= s < ent['gpu_offset'] + ent['gpu_size']:
            hit = ent
            break
    if hit is None:
        outside += e - s
        continue
    key = ('albedo' if hit['file_id'] in albedo else 'othertex', hit['file_id'])
    d = buckets.setdefault(key, [0, 0, None, None])
    d[0] += e - s
    d[1] += 1
    d[2] = hit['gpu_offset']
    d[3] = hit['gpu_size']

print()
print('%-10s %-18s %-10s %s' % ('kind', 'texId', 'bytes', 'runs'))
print('-' * 60)
for (kind, tid), d in sorted(buckets.items()):
    print('%-10s 0x%016x %-10d %d' % (kind, tid, d[0], d[1]))
print()
print('bytes outside any texture entry: %d' % outside)
bad = [k for k in buckets if k[0] != 'albedo']
print('RESULT: %s' % ('PASS - only albedo pixel blocks changed'
                      if (not bad and outside == 0) else
                      'FAIL - unexpected ranges: %s (outside=%d)' % (bad, outside)))
