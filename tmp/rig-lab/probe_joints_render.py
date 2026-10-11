"""Draw the source skeleton's bone ORIGINS on top of the source mesh.

`probe_chain.py` reported numbers that look anatomically odd (source Pelvis joint
z = 0.819 while its own pelvis mesh centroid sits at 1.082).  Before trusting any
"land the joint here" rule we need to see where the joints actually are.  Also
prints, per bone, the joint height and the height of the mesh it dominates, so the
offset is explicit.
"""
import argparse
import os
import sys

from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E

PW, PH = 480, 620


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--vpk', required=True)
    ap.add_argument('--model', required=True)
    ap.add_argument('--outdir', default='.')
    ap.add_argument('--out', required=True)
    ap.add_argument('--bones', default='')
    a = ap.parse_args()

    _v, do, ents = E.parse_vpk_tree(a.vpk)
    by = {f.lower(): (f, arch, o, l) for f, arch, o, l in ents}
    cands = [f for f in by if f.lower().endswith('.mdl')
             and a.model.lower() in os.path.basename(f).lower()]
    cands.sort(key=lambda f: (len(os.path.basename(f)), os.path.basename(f)))
    mdl_path = cands[0]
    base = mdl_path[:-4]

    def blob(p):
        f, arch, o, l = by[p.lower()]
        return E.read_range(a.vpk, do, arch, o, l)

    mdl = blob(mdl_path)
    vvd = blob(base + '.vvd')
    vtx = blob(next(p for p in by if p.lower() == (base + '.dx90.vtx').lower()))
    mdl_parts = E.read_mdl_geometry(mdl)
    vvd_verts, _lc, _n = E.read_vvd(vvd)
    vtx_parts, _ver, _e8 = E.read_vtx(vtx)
    verts, tris = E.build_geometry(mdl_parts, vvd_verts, vtx_parts, [])
    src_pts = [(v['pos'][0] * E.IN2M, v['pos'][1] * E.IN2M, v['pos'][2] * E.IN2M)
               for v in verts]
    bones = E.read_source_bones(mdl)
    cen = E.source_centroids(verts)

    want = set(x.strip() for x in a.bones.split(',') if x.strip())
    print('%-14s %8s %8s %8s' % ('bone', 'joint z', 'mesh z', 'delta'))
    for b in bones:
        nm = b['name'][len(E.SRC_PREFIX):] if b['name'].startswith(E.SRC_PREFIX) else b['name']
        if want and nm not in want:
            continue
        jz = E.origin(b['world_m'])[2]
        cz = cen.get(b['index'], (0, 0, None))[2]
        print('%-14s %8.3f %8s %8s'
              % (nm, jz, ('%.3f' % cz) if cz is not None else '-',
                 ('%+.3f' % (cz - jz)) if cz is not None else '-'))

    W, H = 2 * PW + 3 * 12, PH + 40
    img = Image.new('RGB', (W, H), (28, 30, 36))
    draw = ImageDraw.Draw(img)
    gb = list(E.bbox(src_pts))
    gb[0] = min(gb[0], -0.01)
    gb[3] = max(gb[3], 0.01)
    gb = tuple(gb)
    sc = min(PW, PH) * (1 - 2 * 0.06) / max(gb[3] - gb[0], gb[5] - gb[2], 0.001)

    for col, (ah, av, ad, flip, label) in enumerate(
            ((0, 2, 1, False, 'front (X-Z)'), (1, 2, 0, True, 'side (Y-Z)'))):
        x0 = 12 + col * (PW + 12)
        draw.text((x0 + 4, 20), label, fill=(200, 210, 225))
        E.render_panel(img, draw, src_pts, tris, gb, ah, av, ad, flip,
                       (x0, 36, x0 + PW, 36 + PH), (24, 26, 32), sc)
        # overlay joints
        x1, y1 = x0 + PW, 36 + PH
        w, h = PW, PH
        cx = (gb[ah] + gb[3 + ah]) / 2.0
        cv = (gb[av] + gb[3 + av]) / 2.0

        def T(p):
            u = (p[ah] - cx) * sc
            v = (p[av] - cv) * sc
            if flip:
                u = -u
            return (x0 + w / 2.0 + u, 36 + h / 2.0 - v)

        for b in bones:
            nm = b['name'][len(E.SRC_PREFIX):] if b['name'].startswith(E.SRC_PREFIX) else b['name']
            if want and nm not in want:
                continue
            j = E.origin(b['world_m'])
            px, py = T(j)
            if not (x0 <= px <= x1 and 36 <= py <= y1):
                continue
            has_mesh = b['index'] in cen
            colr = (255, 90, 90) if has_mesh else (90, 160, 255)
            draw.ellipse([px - 3, py - 3, px + 3, py + 3], fill=colr)
            draw.text((px + 5, py - 5), nm, fill=(255, 220, 140))
            if has_mesh:
                m = cen[b['index']]
                mx, my = T(m)
                draw.line([px, py, mx, my], fill=(255, 90, 90))
                draw.ellipse([mx - 2, my - 2, mx + 2, my + 2], outline=(255, 90, 90))

    os.makedirs(a.outdir, exist_ok=True)
    out = os.path.join(a.outdir, a.out)
    img.save(out)
    print('[written] %s' % out)


if __name__ == '__main__':
    main()
