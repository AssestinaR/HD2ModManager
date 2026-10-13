# -*- coding: utf-8 -*-
"""Per-slot texture census reporting the *luminance* statistics (masks live in
RGB, not in alpha) plus the alpha statistics, and dumping both views.

usage: _probe_maskcensus.py <patch_0> <out_prefix>
"""
import os
import subprocess
import sys
import tempfile
import shutil
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A
import hd2_patch as P

TC = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0\deps\texconv.exe')
OUT = Path(__file__).resolve().parent / 'out' / 'mask'

KNOWN_SLOT = {
    0xa72cb013: 'BloodTiler', 0x479fb1ef: 'SclarFieldOpacity', 0x4c567810: 'Nar',
    0x90b84a53: 'IlluminateData', 0xe719da57: 'DetailNormalLeather',
    0xe67ac0c7: 'AlbedoEmissive', 0x736a0029: 'WoundNormal',
    0x04fb61ad: 'DetailNormalPorcelain', 0xd2f99d38: 'WeatheringSpecial',
    0xf8e31d7b: 'WoundData', 0xa59f5e11: 'WoundDerivative',
    0x7ca0d044: '?0', 0xc985395a: 'EmissiveColor', 0xdf3ee984: '?4',
    0xcaed6cd6: 'Normal', 0xe7bd9019: '?7', 0xd47db28b: '?8', 0xff2c91cc: '?9',
    0x1d57dcf3: 'NormalXyAoRoughMap', 0xcbde381b: 'OpacityClipMap',
    0x848ba63b: 'BaseColorMetalMap', 0xdab4d0ee: 'NormalXyRoughnessOpacity',
    0x756f6fa6: 'Mra', 0xac652e43: 'Albedo',
}
OPACITY_VARS = {0xcb8c5728: 'OpacitySharpness', 0xccddd706: 'OpacityOffset',
                0x529a4aaf: 'OpacityThreshold', 0xab58e70d: 'OpacityBase'}


def decode(t, tmp, tag):
    from PIL import Image
    dds = os.path.join(tmp, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(bytes(t.dds_header) + bytes(t.pixels))
    subprocess.run([str(TC), '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
                    '-m', '1', '-o', tmp, dds], capture_output=True)
    p = os.path.join(tmp, tag + '.png')
    if not os.path.exists(p):
        return None, None
    return np.asarray(Image.open(p).convert('RGBA'), np.uint8), p


def stats(arr):
    al = arr[:, :, 3].astype(np.float64)
    rgb = arr[:, :, :3].astype(np.float64)
    lum = 0.2126 * rgb[:, :, 0] + 0.7152 * rgb[:, :, 1] + 0.0722 * rgb[:, :, 2]
    return dict(
        a_mean=float(al.mean()), a_zero=100.0 * float((al == 0).mean()),
        a_full=100.0 * float((al == 255).mean()),
        a_mid=100.0 * float(((al > 0) & (al < 255)).mean()),
        l_mean=float(lum.mean()), l_lo=100.0 * float((lum < 26).mean()),
        l_hi=100.0 * float((lum > 230).mean()),
        l_mid=100.0 * float(((lum >= 26) & (lum <= 230)).mean()),
        gray=float(np.abs(rgb[:, :, 0] - rgb[:, :, 1]).mean()
                   + np.abs(rgb[:, :, 1] - rgb[:, :, 2]).mean()) / 2.0)


def main():
    patch = sys.argv[1]
    prefix = sys.argv[2] if len(sys.argv) > 2 else 'mask'
    OUT.mkdir(parents=True, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix='maskcensus')
    entries, _ = P.scan_entries(patch)

    tex_by_id = {}
    for e in entries:
        if e['type_id'] == A.TEXTURE_TYPE_ID:
            pl = P.entry_payload(e, patch)
            tex_by_id[e['file_id']] = (A.Texture.read(pl['toc'], pl['gpu']), pl)

    print('patch %s : %d textures' % (patch, len(tex_by_id)))
    for e in entries:
        if e['type_id'] != A.MATERIAL_TYPE_ID:
            continue
        m = A.Material.read(P.entry_payload(e, patch)['toc'])
        print('=' * 78)
        print('material 0x%016x  parent=%s (0x%016x)' % (e['file_id'], m.parent_name,
                                                         m.parent_material_id))
        for v in m.variables:
            if v.id in OPACITY_VARS:
                print('   OPACITY VAR  %-18s = %s'
                      % (OPACITY_VARS[v.id], ['%.6g' % x for x in v.values]))
        for i, (k, tid) in enumerate(zip(m.tex_unks, m.tex_ids)):
            nm = KNOWN_SLOT.get(k, '?')
            hit = tex_by_id.get(tid)
            if not hit:
                print('   slot %2d %-22s tex 0x%016x  base-game (not in patch)'
                      % (i, nm, tid))
                continue
            t, pl = hit
            arr, png = decode(t, tmp, '%016x' % tid)
            if arr is None:
                print('   slot %2d %-22s tex 0x%016x  DECODE FAIL' % (i, nm, tid))
                continue
            s = stats(arr)
            dst = OUT / ('%s_%016x.png' % (prefix, tid))
            shutil.copy2(png, dst)
            print('   slot %2d %-22s tex 0x%016x %4dx%-4d %-22s '
                  'A(m%.0f 0:%.0f%% 255:%.0f%% mid:%.0f%%) '
                  'L(m%.1f lo:%.1f%% hi:%.1f%% mid:%.1f%%) gray=%.1f'
                  % (i, nm, tid, t.width, t.height, t.format_name,
                     s['a_mean'], s['a_zero'], s['a_full'], s['a_mid'],
                     s['l_mean'], s['l_lo'], s['l_hi'], s['l_mid'], s['gray']))
    return 0


if __name__ == '__main__':
    sys.exit(main())
