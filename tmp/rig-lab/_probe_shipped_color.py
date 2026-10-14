# -*- coding: utf-8 -*-
"""Compare the source base-colour VTF against the texture that actually ships
in our HD2 patch (slot 5 albedo), for the same material group.

Also dumps side-by-side PNGs so the difference is visible.

usage: _probe_shipped_color.py [patch_0]
"""
import os, sys, struct, tempfile, subprocess
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_asset as A
import hd2_patch as HP
import hd2_vtf as V
import hd2_texpack as TP
import hd2_source as S
from PIL import Image

TEXCONV = TP.TEXCONV
OUT = r'E:/tmp/r71-color'
os.makedirs(OUT, exist_ok=True)


def _run(cmd):
    p = subprocess.run(cmd, capture_output=True)
    if p.returncode != 0:
        raise RuntimeError('%s\n%s' % (cmd, p.stderr.decode('utf-8', 'replace')))
    return p


def decode_dds(dds_path, srgb, tag):
    d = os.path.dirname(dds_path)
    stem = os.path.splitext(os.path.basename(dds_path))[0]
    png = os.path.join(d, stem + '.png')
    if os.path.exists(png):
        os.remove(png)
    fmt = 'R8G8B8A8_UNORM_SRGB' if srgb else 'R8G8B8A8_UNORM'
    _run([TEXCONV, '-nologo', '-y', '-ft', 'png', '-f', fmt, '-m', '1', '-o', d, dds_path])
    if not os.path.exists(png):
        return None
    return np.asarray(Image.open(png).convert('RGBA'), np.uint8)


def stats(a, label):
    if a is None:
        print('    %-24s <none>' % label); return
    r = a[:, :, 0].astype(np.float64); g = a[:, :, 1].astype(np.float64); b = a[:, :, 2].astype(np.float64)
    print('    %-24s mean %6.1f %6.1f %6.1f | min %3d %3d %3d | max %3d %3d %3d'
          % (label, r.mean(), g.mean(), b.mean(), r.min(), g.min(), b.min(), r.max(), g.max(), b.max()))


def main():
    patch = sys.argv[1] if len(sys.argv) > 1 else \
        r'E:/mod/Lilium-Sancta-RE2310/9ba626afa44a3aa3.patch_0'
    tmp = tempfile.mkdtemp(prefix='shipped')
    packs = S.Packs()
    entries, _raw = HP.scan_entries(patch)
    tex = {e['file_id']: e for e in entries if e['type_id'] == A.TEXTURE_TYPE_ID}
    mats = {e['file_id']: e for e in entries if e['type_id'] == A.MATERIAL_TYPE_ID}

    print('patch: %d tex / %d mat' % (len(tex), len(mats)))

    # find the material our unit uses, and read its slot table
    names = A.__dict__.get('MATERIAL_TYPE_ID')
    for mid, me in list(mats.items())[:40]:
        pl = HP.entry_payload(me, patch)
        m = A.Material.read(pl['toc'])
        if len(m.tex_ids) != 11:
            continue
        color_tid = m.tex_ids[5]
        if color_tid not in tex:
            continue
        te = tex[color_tid]
        pl2 = HP.entry_payload(te, patch)
        t = A.Texture.read(pl2['toc'], pl2['gpu'])
        if len(t.pixels) == 0:
            continue
        w = struct.unpack_from('<I', bytes(t.dds_header), 16)[0]
        h = struct.unpack_from('<I', bytes(t.dds_header), 12)[0]
        if w < 64:
            continue
        dds = os.path.join(tmp, 'ship_%016x.dds' % color_tid)
        open(dds, 'wb').write(t.to_dds())
        arr = decode_dds(dds, srgb=True, tag='ship')
        print('  material 0x%016x  slot5 tex 0x%016x  %dx%d' % (mid, color_tid, w, h))
        stats(arr, 'SHIPPED albedo (sRGB)')
        if arr is not None:
            Image.fromarray(arr[:, :, :3], 'RGB').resize((512, 512), Image.LANCZOS).save(
                os.path.join(OUT, 'shipped_%016x.png' % mid))
        break
    print('out:', OUT)


if __name__ == '__main__':
    main()
