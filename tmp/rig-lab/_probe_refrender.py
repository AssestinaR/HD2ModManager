# -*- coding: utf-8 -*-
"""Render a shipped unit textured, both V conventions -- control experiment.

Read-only.  The reference mod `【4.28】Chiffon-LUNALICE替换RE-2310` renders
correctly in game, so it is the calibration target: whatever V convention makes
*it* look right is HD2's convention.  Then render our own unit the same way and
see whether the hair lands on the same convention.

usage:  _probe_refrender.py <patch_0> <unit_id_hex> <out_tag> [res]
"""
import os
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A
import hd2_patch as P
import hd2_unit as U

SDK = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0')
TEXCONV = SDK / 'deps' / 'texconv.exe'
OUT = Path(r'E:\Data\source\repos\WpfApp1\tmp\rig-lab\out\uv-crops')

PATCH = sys.argv[1] if len(sys.argv) > 1 else \
    r'E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0'
UNIT = int(sys.argv[2], 16) if len(sys.argv) > 2 else 0xCAE35EE15168145F
TAG = sys.argv[3] if len(sys.argv) > 3 else 'ref'
RES = int(sys.argv[4]) if len(sys.argv) > 4 else 700
ZBAND = None
if len(sys.argv) > 5 and sys.argv[5] != '-':
    _a, _b = sys.argv[5].split(',')
    ZBAND = (float(_a), float(_b))

# advanced template: slot 5 = base colour
BASE_SLOT = 5


def decode_tex(toc, gpu, tmp, tag):
    t = A.Texture.read(toc, gpu)
    dds = os.path.join(tmp, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(bytes(t.dds_header) + bytes(gpu))
    subprocess.run([str(TEXCONV), '-nologo', '-y', '-ft', 'png',
                    '-f', 'R8G8B8A8_UNORM', '-m', '1', '-o', tmp, dds],
                   capture_output=True)
    png = os.path.join(tmp, tag + '.png')
    if not os.path.exists(png):
        return None
    from PIL import Image
    return np.asarray(Image.open(png).convert('RGB'), np.float64) / 255.0


def render(pos, uv, idx, tri_tex, res, flip_v, zband=None):
    # HD2: +X right, +Y forward, +Z up -> front view looks down -Y
    x, y, z = pos[:, 0], pos[:, 1], pos[:, 2]
    zmin, zmax = z.min(), z.max()
    zlo, zhi = zmin, zmax
    if zband:
        f0, f1 = zband
        zlo = zmin + f0 * (zmax - zmin)
        zhi = zmin + f1 * (zmax - zmin)
    sel = (z >= zlo) & (z <= zhi)
    lo = np.array([x[sel].min(), zlo])
    hi = np.array([x[sel].max(), zhi])
    pad = 0.02 * (hi - lo).max()
    lo -= pad
    hi += pad
    sx = (x - lo[0]) / (hi[0] - lo[0]) * (res - 1)
    sy = (res - 1) - (z - lo[1]) / (hi[1] - lo[1]) * (res - 1)

    color = np.full((res, res, 3), 0.12)
    zbuf = np.full((res, res), -1e18)
    order = np.argsort(y[idx[:, :3]].mean(axis=1))
    for t in order:
        a, b, c = idx[t, :3]
        im = tri_tex[t]
        if im is None:
            continue
        H, W = im.shape[:2]
        px = np.array([sx[a], sx[b], sx[c]])
        py = np.array([sy[a], sy[b], sy[c]])
        pz = np.array([y[a], y[b], y[c]])
        x0, x1 = int(max(0, np.floor(px.min()))), int(min(res - 1, np.ceil(px.max())))
        y0, y1 = int(max(0, np.floor(py.min()))), int(min(res - 1, np.ceil(py.max())))
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
        u = w0 * uv[a, 0] + w1 * uv[b, 0] + w2 * uv[c, 0]
        vv = w0 * uv[a, 1] + w1 * uv[b, 1] + w2 * uv[c, 1]
        u = np.mod(u, 1.0)
        vv = np.mod(vv, 1.0)
        if flip_v:
            vv = 1.0 - vv
        xi = np.clip((u * W).astype(np.int64), 0, W - 1)
        yi = np.clip((vv * H).astype(np.int64), 0, H - 1)
        samp = im[yi, xi]
        cimg = color[y0:y1 + 1, x0:x1 + 1]
        cimg[m] = samp[m]
        sub[m] = depth[m]
    return color


def main():
    from PIL import Image
    OUT.mkdir(parents=True, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix='refrender')
    entries, _ = P.scan_entries(PATCH)
    tex = {e['file_id']: e for e in entries if e['type_id'] == A.TEXTURE_TYPE_ID}
    mats = {e['file_id']: e for e in entries if e['type_id'] == A.MATERIAL_TYPE_ID}

    e = next(x for x in entries if x['type_id'] == U.UNIT_ID and x['file_id'] == UNIT)
    pl = P.entry_payload(e, PATCH)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    print('unit 0x%016x  %d streams / %d meshes'
          % (UNIT, len(u.stream_infos), len(u.mesh_infos)))

    vs = u.decode_vertices(0)
    pos = np.asarray([v['position'] for v in vs], np.float64)
    uv = np.asarray([[v['uv0'][0], v['uv0'][1]] for v in vs], np.float64)
    idx = np.asarray(u.index_list(0), np.int64).reshape(-1, 3)
    print('lod0: %d verts, %d tris' % (len(pos), len(idx)))

    mi = next(m for m in u.mesh_infos if m.lod_index == 0)
    tri_tex = np.empty(len(idx), dtype=object)
    seen = {}
    for sec in mi.sections:
        if sec.num_indices == 0:
            continue
        sid = mi.material_ids[sec.material_index]
        asset = u.material_ids[u.section_ids.index(sid)]
        me = mats.get(asset)
        img = None
        if me is not None:
            m = A.Material.read(P.entry_payload(me, PATCH)['toc'])
            tid = m.tex_ids[BASE_SLOT]
            te = tex.get(tid)
            if te is not None:
                tp = P.entry_payload(te, PATCH)
                img = decode_tex(tp['toc'], tp['gpu'], tmp, '%016x' % tid)
        seen[sec.material_index] = (asset, None if img is None else img.shape)
        s = sec.index_offset // 3
        n = sec.num_indices // 3
        for t in range(s, s + n):
            tri_tex[t] = img
        print('  sec mat_index=%2d  tris %6d  tex %s'
              % (sec.material_index, n,
                 'none' if img is None else '%dx%d' % img.shape[:2]))

    for flip, tag in ((False, 'verbatim'), (True, 'flipped')):
        img = render(pos, uv, idx, tri_tex, RES, flip, ZBAND)
        p = OUT / ('%s-%s.png' % (TAG, tag))
        Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8)).save(p)
        print('%-9s -> %s' % (tag, p))

    a = Image.open(OUT / ('%s-verbatim.png' % TAG))
    b = Image.open(OUT / ('%s-flipped.png' % TAG))
    sheet = Image.new('RGB', (a.width + b.width + 6, a.height), (28, 32, 38))
    sheet.paste(a, (0, 0))
    sheet.paste(b, (a.width + 6, 0))
    sheet.save(OUT / ('%s-both.png' % TAG))
    print('both ->', OUT / ('%s-both.png' % TAG))
    return 0


if __name__ == '__main__':
    sys.exit(main())
