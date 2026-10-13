# -*- coding: utf-8 -*-
"""Measure the alignment between a section's uv ISLAND BOUNDARY and the artwork
it samples -- objectively, in texture pixels.

Only boundary edges are used: an edge shared by two triangles in the same
section is interior triangulation (it aligns with nothing), while an edge used
by exactly one triangle is the silhouette of a uv island.  For a hair card the
island silhouette follows the strand's alpha silhouette, so the boundary must
hug the artwork outline.

We rasterise the boundary, build a distance transform of the artwork's edge map,
read the mean distance at zero offset, then search offsets for the minimum:

  minimum at (0,0)   -> the overlay is exactly where the data says it is
  minimum elsewhere  -> the overlay really is displaced by that much

usage: _probe_uvalign.py <patch_0> <unit_hex> [sections_csv] [out_tag]
"""
import os
import subprocess
import sys
import tempfile
from collections import Counter
from pathlib import Path

import numpy as np
from scipy.ndimage import distance_transform_edt

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A
import hd2_patch as P
import hd2_unit as U

SDK = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0')
TEXCONV = SDK / 'deps' / 'texconv.exe'
OUT = Path(r'E:\Data\source\repos\WpfApp1\tmp\rig-lab\out\uv-crops')

NAMES = {
    0xb9f84331454d8282: 'shinano_face', 0xbe569f23c33c3083: 'white',
    0xbde6be9e588bc941: '_4', 0xb8a1ba9aed375bed: 'ornament_metal',
    0xb6e760aee076359d: 'shinano_body', 0xb66b54bafd3dd768: 'shinano_hair_ear',
    0xbbd058dd84a87e8c: 'headdress_white01_catears', 0xbea2867f775bc39b: 'lace_white01_catears',
    0xbb106a56c4d48a2b: 'bra_white01_bra_inner', 0xb3ba47e7968a0ab5: 'glove_white01_wristcuffs',
    0xb8f74de60c5f8803: 'dress_white01_dress_corset', 0xbc9d1471d87f8337: 'lower_white01_underwear',
    0xb65a1090f1511be3: 'shoes_white01_highheels', 0xbf29404cde77bd86: 'shoes_02',
}
BASE_SLOT = 5


def decode_gray(toc, gpu, tmp, tag):
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
    return np.asarray(Image.open(png).convert('L'), np.float64) / 255.0


def boundary_edges(seg):
    """seg -> list of (i,j) vertex-index pairs used by exactly one triangle."""
    cnt = Counter()
    for k in range(0, len(seg), 3):
        a, b, c = int(seg[k]), int(seg[k + 1]), int(seg[k + 2])
        for p, q in ((a, b), (b, c), (c, a)):
            cnt[(p, q) if p < q else (q, p)] += 1
    return [e for e, n in cnt.items() if n == 1]


def main():
    patch = sys.argv[1]
    unit = int(sys.argv[2], 16)
    only = None
    if len(sys.argv) > 3 and sys.argv[3] != '-':
        only = set(int(x) for x in sys.argv[3].split(','))
    tag = sys.argv[4] if len(sys.argv) > 4 else 'bound'

    from PIL import Image, ImageDraw
    tmp = tempfile.mkdtemp(prefix='uvalign')
    entries, _ = P.scan_entries(patch)
    tex = {e['file_id']: e for e in entries if e['type_id'] == A.TEXTURE_TYPE_ID}
    mats = {e['file_id']: e for e in entries if e['type_id'] == A.MATERIAL_TYPE_ID}
    e = next(x for x in entries if x['type_id'] == U.UNIT_ID and x['file_id'] == unit)
    pl = P.entry_payload(e, patch)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    vs = u.decode_vertices(0)
    uv = np.asarray([[v['uv0'][0], v['uv0'][1]] for v in vs], np.float64)
    idx = np.asarray(u.index_list(0), np.int64)
    mi = next(m for m in u.mesh_infos if m.lod_index == 0)

    OFF = 96
    tiles = []
    print('%-4s %-30s %9s %10s %10s %s'
          % ('sec', 'texture', 'boundary', 'dist@(0,0)', 'dist@best', 'best offset (px / uv)'))
    for si, sec in enumerate(mi.sections):
        if sec.num_indices == 0:
            continue
        if only is not None and si not in only:
            continue
        sid = mi.material_ids[sec.material_index]
        asset = u.material_ids[u.section_ids.index(sid)]
        me = mats.get(asset)
        if me is None:
            continue
        m = A.Material.read(P.entry_payload(me, patch)['toc'])
        tid = m.tex_ids[BASE_SLOT] if len(m.tex_ids) > BASE_SLOT else 0
        te = tex.get(tid)
        if te is None:
            continue
        tp = P.entry_payload(te, patch)
        g = decode_gray(tp['toc'], tp['gpu'], tmp, '%016x' % tid)
        if g is None or g.shape[0] < 256:
            continue
        H, W = g.shape

        gx = np.zeros_like(g); gy = np.zeros_like(g)
        gx[:, 1:-1] = g[:, 2:] - g[:, :-2]
        gy[1:-1, :] = g[2:, :] - g[:-2, :]
        mag = np.hypot(gx, gy)
        edges = mag >= max(np.percentile(mag, 90), 0.04)
        dist = distance_transform_edt(~edges)

        seg = idx[sec.index_offset:sec.index_offset + sec.num_indices]
        be = boundary_edges(seg)
        if len(be) < 50:
            continue
        im = Image.new('L', (W, H), 0)
        dr = ImageDraw.Draw(im)
        px = (uv[:, 0] % 1.0) * W
        py = (uv[:, 1] % 1.0) * H
        for p, q in be:
            dr.line([float(px[p]), float(py[p]), float(px[q]), float(py[q])],
                    fill=255, width=1)
        wf = np.asarray(im) > 0
        ys, xs = np.nonzero(wf)
        n = len(ys)
        if n < 200:
            continue
        if n > 40000:
            pick = np.random.default_rng(0).choice(n, 40000, replace=False)
            ys, xs = ys[pick], xs[pick]

        def score(dy, dx):
            yy = np.clip(ys + dy, 0, H - 1)
            xx = np.clip(xs + dx, 0, W - 1)
            return dist[yy, xx].mean()

        base = score(0, 0)
        best = (base, 0, 0)
        for step, rad in ((12, OFF), (4, 12), (1, 4)):
            cy, cx = best[1], best[2]
            for dy in range(cy - rad, cy + rad + 1, step):
                for dx in range(cx - rad, cx + rad + 1, step):
                    if abs(dy) > OFF or abs(dx) > OFF:
                        continue
                    s = score(dy, dx)
                    if s < best[0]:
                        best = (s, dy, dx)
        print('sec%-3d %-30s %9d %10.2f %10.2f  (dx=%+4d, dy=%+4d) uv(%+.4f, %+.4f)'
              % (si, NAMES.get(tid, '?'), len(be), base, best[0],
                 best[2], best[1], best[2] / W, best[1] / H))

        # visual: artwork dimmed + boundary in red (full res, then shrink)
        base_img = Image.fromarray((np.clip(g, 0, 1) * 190).astype(np.uint8)).convert('RGB')
        d2 = ImageDraw.Draw(base_img)
        for p, q in be:
            d2.line([float(px[p]), float(py[p]), float(px[q]), float(py[q])],
                    fill=(255, 40, 40), width=2)
        base_img.thumbnail((620, 620))
        tiles.append(('sec%d %s  boundary, dist@0=%.1fpx' % (si, NAMES.get(tid, '?'), base), base_img))

    if tiles:
        CELL = 630
        cols = 2
        rows = (len(tiles) + cols - 1) // cols
        sheet = Image.new('RGB', (cols * CELL, rows * (CELL + 20)), (28, 32, 38))
        dd = ImageDraw.Draw(sheet)
        for k, (lab, im) in enumerate(tiles):
            r, c = divmod(k, cols)
            sheet.paste(im, (c * CELL + 4, r * (CELL + 20) + 4))
            dd.text((c * CELL + 4, r * (CELL + 20) + CELL + 2), lab, fill=(235, 240, 246))
        p = OUT / ('uvalign-%s.png' % tag)
        sheet.save(p)
        print('->', p)
    return 0


if __name__ == '__main__':
    sys.exit(main())
