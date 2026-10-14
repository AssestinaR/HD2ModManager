# -*- coding: utf-8 -*-
"""Which HD2 material maps to which source group, and which albedo texture
each one uses.  Needed to know what we are darkening."""
import sys, os, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_asset as A
import hd2_patch as HP

P = r'E:/mod/Lilium-Sancta-RE2310/9ba626afa44a3aa3.patch_0'
MF = r'E:/tmp/lilium-final/lilium-sancta-re2310.manifest.json'

e, _ = HP.scan_entries(P)
mf = json.load(open(MF, encoding='utf-8'))
g2info = {}
for g in mf['groups']:
    g2info[int(g['material'], 16)] = (g['group'], g['base'].split('\\')[-1],
                                      g['template'], g['matcapDominant'])

print('%-18s %-8s %-18s %-18s %-12s %s' % ('material', 'slots', 'albedo tex',
                                           'group', 'template', 'matcapDom'))
print('-' * 100)
rows = []
for m in e:
    if m['type_id'] != A.MATERIAL_TYPE_ID:
        continue
    mm = A.Material.read(HP.entry_payload(m, P)['toc'])
    n = len(mm.tex_ids)
    gi = g2info.get(m['file_id'], ('?', '?', '?', None))
    if n == 11:
        alb = mm.tex_ids[5]
    elif n == 3:
        alb = mm.tex_ids[2]
    else:
        alb = None
    rows.append((m['file_id'], n, alb, gi))
for fid, n, alb, gi in sorted(rows, key=lambda r: (r[3][0] or '')):
    print('0x%016x %-8d %-18s %-18s %-12s %s'
          % (fid, n, ('0x%016x' % alb) if alb else '-', gi[0], gi[2], gi[3]))
