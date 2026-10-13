# -*- coding: utf-8 -*-
"""Render the unit with every mesh section in its own flat colour.

Read-only.  Needed because the material names are misleading: `shinano_hair_ear`
covers only 1080 triangles, so the long hair must belong to some other section.
Colour-coding by section says which one, without guessing from names.

usage: _probe_secmap.py <patch_0> <unit_hex> <out_tag> [res] [zband]
"""
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_patch as P
import hd2_unit as U

OUT = Path(r'E:\Data\source\repos\WpfApp1\tmp\rig-lab\out\uv-crops')

PATCH = sys.argv[1] if len(sys.argv) > 1 else \
    r'E:/tmp/rig-lab/cand-m4w/9ba626afa44a3aa3.patch_0'
UNIT = int(sys.argv[2], 16) if len(sys.argv) > 2 else 0xCAE35EE15168145F
TAG = sys.argv[3] if len(sys.argv) > 3 else 'secmap'
RES = int(sys.argv[4]) if len(sys.argv) > 4 else 900
ZBAND = None
if len(sys.argv) > 5 and sys.argv[5] != '-':
    _a, _b = sys.argv[5].split(',')
    ZBAND = (float(_a), float(_b))

PALETTE = [
    (0.95, 0.25, 0.25), (0.25, 0.85, 0.35), (0.25, 0.45, 0.95), (0.95, 0.85, 0.20),
    (0.85, 0.30, 0.85), (0.20, 0.85, 0.85), (0.95, 0.55, 0.15), (0.55, 0.35, 0.85),
    (0.35, 0.65, 0.30), (0.85, 0.45, 0.60), (0.45, 0.45, 0.45), (0.20, 0.35, 0.55),
    (0.70, 0.70, 0.95), (0.95, 0.95, 0.95), (0.60, 0.20, 0.20), (0.20, 0.60, 0.40),
]


def main():
    from PIL import Image, ImageDraw
    OUT.mkdir(parents=True, exist_ok=True)
    entries, _ = P.scan_entries(PATCH)
    e = next(x for x in entries if x['type_id'] == U.UNIT_ID and x['file_id'] == UNIT)
    pl = P.entry_payload(e, PATCH)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    vs = u.decode_vertices(0)
    pos = np.asarray([v['position'] for v in vs], np.float64)
    idx = np.asarray(u.index_list(0), np.int64).reshape(-1, 3)
    mi = next(m for m in u.mesh_infos if m.lod_index == 0)

    sec_of_tri = np.full(len(idx), -1, np.int32)
    for si, sec in enumerate(mi.sections):
        if sec.num_indices == 0:
            continue
        s = sec.index_offset // 3
        sec_of_tri[s:s + sec.num_indices // 3] = si

    x, y, z = pos[:, 0], pos[:, 1], pos[:, 2]
    zmin, zmax = z.min(), z.max()
    zlo, zhi = zmin, zmax
    if ZBAND:
        zlo = zmin + ZBAND[0] * (zmax - zmin)
        zhi = zmin + ZBAND[1] * (zmax - zmin)
    sel = (z >= zlo) & (z <= zhi)
    lo = np.array([x[sel].min(), zlo])
    hi = np.array([x[sel].max(), zhi])
    pad = 0.02 * (hi - lo).max()
    lo -= pad
    hi += pad
    sx = (x - lo[0]) / (hi[0] - lo[0]) * (RES - 1)
    sy = (RES - 1) - (z - lo[1]) / (hi[1] - lo[1]) * (RES - 1)

    color = np.full((RES, RES, 3), 0.10)
    zbuf = np.full((RES, RES), -1e18)
    order = np.argsort(y[idx[:, :3]].mean(axis=1))
    for t in order:
        a, b, c = idx[t]
        si = sec_of_tri[t]
        if si < 0:
            continue
        col = np.array(PALETTE[si % len(PALETTE)])
        px = np.array([sx[a], sx[b], sx[c]])
        py = np.array([sy[a], sy[b], sy[c]])
        pz = np.array([y[a], y[b], y[c]])
        x0, x1 = int(max(0, np.floor(px.min()))), int(min(RES - 1, np.ceil(px.max())))
        y0, y1 = int(max(0, np.floor(py.min()))), int(min(RES - 1, np.ceil(py.max())))
        if x1 < x0 or y1 < y0:
            continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1), np.arange(y0, y1 + 1))
        d = ((py[1] - py[2]) * (px[0] - px[2]) + (px[2] - px[1]) * (py[0] - py[2]))
        if abs(d) < 1e-12:
            continue
        w0 = ((py[1] - py[2]) * (gx - px[2]) + (px[2] - px[1]) * (gy - py[2])) / d
        w1 = ((py[2] - py[0]) * (gx - px[2]) + (px[0] - px[2]) * (gy - py[2])) / d
        w2 = 1.0 - w0 - w1
        m = (w0 >= -1e-6) & (w1 >= -1e-6) & (w2 >= -1e-6)
        if not m.any():
            continue
        depth = w0 * pz[0] + w1 * pz[1] + w2 * pz[2]
        sub = zbuf[y0:y1 + 1, x0:x1 + 1]
        m &= depth > sub
        if not m.any():
            continue
        cimg = color[y0:y1 + 1, x0:x1 + 1]
        cimg[m] = col
        sub[m] = depth[m]

    im = Image.fromarray((np.clip(color, 0, 1) * 255).astype(np.uint8))
    dr = ImageDraw.Draw(im)
    for si, sec in enumerate(mi.sections):
        col = tuple(int(255 * v) for v in PALETTE[si % len(PALETTE)])
        dr.rectangle([8, 8 + si * 20, 26, 24 + si * 20], fill=col)
        dr.text((32, 10 + si * 20),
                'sec %2d   %6d tris' % (si, sec.num_indices // 3),
                fill=(240, 244, 250))
    p = OUT / ('%s.png' % TAG)
    im.save(p)
    print('section map ->', p)
    return 0


if __name__ == '__main__':
    sys.exit(main())
