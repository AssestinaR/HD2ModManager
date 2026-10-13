# -*- coding: utf-8 -*-
"""Textured orthographic render of the SOURCE model, both V conventions.

Read-only.  UV bytes are provably identical to the source, so the only way the
hair can sample the wrong corner is a V-axis convention mismatch: Source puts
(0,0) at the bottom-left, DDS/DirectX put row 0 at the top.  Copying V verbatim
therefore mirrors every material vertically.

Full-span materials (0.99 x 0.99) hide that completely; the hair's 0.2 x 0.175
corner island does not.

This renders the model flat-shaded with a straight texture lookup:
  * `verbatim`  row = v*H        (what a verbatim copy does)
  * `flipped`   row = (1-v)*H    (Source -> DirectX convention)
Whichever shows hair on the head is the convention the game needs.
"""
import os
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import export_obj as E
import hd2_source as S
import hd2_style as ST
import hd2_texpack as TP
import hd2_vtf as V

SDK = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0')
TEXCONV = SDK / 'deps' / 'texconv.exe'
OUT = Path(r'E:\Data\source\repos\WpfApp1\tmp\rig-lab\out\uv-crops')

VPK = S.ADDONS + S.MODEL_VPK
MODEL = 'survivor_teenangst'
RES = int(sys.argv[1]) if len(sys.argv) > 1 else 640
ZBAND = None
if len(sys.argv) > 2 and sys.argv[2] != '-':
    _a, _b = sys.argv[2].split(',')
    ZBAND = (float(_a), float(_b))
SUFFIX = sys.argv[3] if len(sys.argv) > 3 else ''


def decode_vtf(raw, tmp, tag):
    v = V.VTF.read(raw)
    hdr, px = TP.vtf_to_dds(raw, TP.DXGI_BC7_SRGB, tag=tag)
    dds = os.path.join(tmp, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(bytes(hdr) + bytes(px))
    subprocess.run([str(TEXCONV), '-nologo', '-y', '-ft', 'png',
                    '-f', 'R8G8B8A8_UNORM', '-m', '1', '-o', tmp, dds],
                   capture_output=True)
    png = os.path.join(tmp, tag + '.png')
    if not os.path.exists(png):
        return None
    from PIL import Image
    return np.asarray(Image.open(png).convert('RGB'), np.float64) / 255.0


def render(verts, tris, tex_by_mi, res, flip_v, zband=None):
    pos = np.asarray([v['pos'] for v in verts], np.float64)
    uv = np.asarray([v['uv'] for v in verts], np.float64)

    # orthographic FRONT view: +Y is forward, so look down -Y; x right, z up
    x, z = pos[:, 0], pos[:, 2]
    zmin, zmax = z.min(), z.max()
    zlo, zhi = zmin, zmax
    if zband:
        zlo = zmin + zband[0] * (zmax - zmin)
        zhi = zmin + zband[1] * (zmax - zmin)
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
    y = pos[:, 1]

    tris = np.asarray(tris, np.int64)
    order = np.argsort(-y[tris[:, :3]].mean(axis=1))     # painter: near first
    for t in order:
        a, b, c, mi = tris[t]
        im = tex_by_mi.get(int(mi))
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
    tmp = tempfile.mkdtemp(prefix='texrender')
    packs = S.Packs()
    _p, mdl, verts, tris, _s, _sh = ST.load_source(VPK, MODEL)
    names, cds, resolved = E.read_mdl_materials(mdl)

    # one decoded texture per DISTINCT basetexture, keyed by material index
    tex_by_mi = {}
    cache = {}
    for mi, nm in enumerate(resolved[0]):
        if nm is None:
            continue
        text = packs.vmt_text(nm)
        vmt = S.parse_vmt(text) if text else {}
        base = vmt.get('$basetexture')
        if not base:
            continue
        if base in cache:
            tex_by_mi[mi] = cache[base]
            continue
        raw = packs.vtf_bytes(base)
        im = decode_vtf(raw, tmp, 'm%02d' % mi) if raw else None
        cache[base] = im
        tex_by_mi[mi] = im
    print('decoded %d distinct textures for %d material slots'
          % (sum(1 for v in cache.values() if v is not None), len(tex_by_mi)))

    for flip, tag in ((False, 'verbatim'), (True, 'flipped')):
        img = render(verts, tris, tex_by_mi, RES, flip, ZBAND)
        p = OUT / ('texrender-%s%s.png' % (tag, SUFFIX))
        Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8)).save(p)
        print('%-9s -> %s' % (tag, p))

    # side by side
    a = Image.open(OUT / ('texrender-verbatim%s.png' % SUFFIX))
    b = Image.open(OUT / ('texrender-flipped%s.png' % SUFFIX))
    sheet = Image.new('RGB', (a.width + b.width + 6, a.height), (28, 32, 38))
    sheet.paste(a, (0, 0))
    sheet.paste(b, (a.width + 6, 0))
    sheet.save(OUT / ('texrender-both%s.png' % SUFFIX))
    print('both ->', OUT / ('texrender-both%s.png' % SUFFIX))
    return 0


if __name__ == '__main__':
    sys.exit(main())
