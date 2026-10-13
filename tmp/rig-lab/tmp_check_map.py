# -*- coding: utf-8 -*-
import os, sys, hashlib
from pathlib import Path
import numpy as np
sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A, hd2_patch as HP, hd2_source as S, hd2_style as ST

MAT_ID_PREFIX = 0xA
def make_id(prefix, name):
    h = hashlib.sha256(name.encode('utf-8')).digest()
    v = int.from_bytes(h[:8], 'little') & 0x0FFFFFFFFFFFFFFF
    return ((prefix & 0xF) << 60) | v

vpk = S.ADDONS + S.MODEL_VPK
_md, mdl, _v, tris4, _s, _sh = ST.load_source(vpk, 'survivor_teenangst')
tris4 = np.asarray(tris4, np.int64)
names, cds, _row = S.load_material_slots(mdl)
used = sorted(int(x) for x in np.unique(tris4[:, 3]))
packs = S.Packs()

groups = {}
for i in used:
    nm = names[i] if 0 <= i < len(names) else None
    txt = packs.vmt_text(nm) if nm else None
    vmt = S.parse_vmt(txt) if txt else {}
    base = vmt.get('$basetexture')
    groups.setdefault(base, []).append(i)

src = r'E:/mod/Lunalice-RE2310-M6/9ba626afa44a3aa3.patch_0'
entries, _ = HP.scan_entries(src)
mats = {e['file_id']: e for e in entries if e['type_id'] == A.MATERIAL_TYPE_ID}
tex  = {e['file_id']: e for e in entries if e['type_id'] == A.TEXTURE_TYPE_ID}
print('patch: %d entries, %d materials, %d textures' % (len(entries), len(mats), len(tex)))
print('=' * 100)
ok = 0
for base, slots in groups.items():
    mid = make_id(MAT_ID_PREFIX, 'mat/' + (base or 'untextured'))
    hit = mid in mats
    if not hit:
        print('MISS  %-34s slots=%2d  id=%016x' % (base, len(slots), mid)); continue
    ok += 1
    m = A.Material.read(HP.entry_payload(mats[mid], src)['toc'])
    col = m.tex_ids[5]; met = m.tex_ids[3]; nrm = m.tex_ids[2]
    te = tex.get(col)
    info = ''
    if te:
        t = A.Texture.read(HP.entry_payload(te, src)['toc'], b'')
        info = '%s %dx%d mips=%d gpu=%d' % (t.format_name, t.width, t.height, t.mip_count, te['gpu_size'])
    print('OK    %-34s slots=%2d id=%016x parent=%-9s col=%016x %s'
          % (base, len(slots), mid, m.parent_name, col, info))
print('=' * 100)
print('matched %d / %d groups' % (ok, len(groups)))
