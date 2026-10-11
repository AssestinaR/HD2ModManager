"""Render the three anchor policies side by side, front + side, one shared scale.

Row 1: the L4D2 source bind pose.
Row 2: anchor=joint   -- each source JOINT is snapped onto its HD2 joint.
Row 3: anchor=neck    -- Neck1/Head1 land their mesh CENTROID on the HD2 joint.
Row 4: anchor=neck+spine -- also Pelvis/Spine/Spine4.

`probe_weights.py` showed the source paints the head region onto Head1 whose JOINT
sits 0.184 m below HD2's head joint, while the head MESH is only 0.057 m off.  So
row 2 stretches the neck and inflates the character ~11 %, and rows 3/4 should
bring the height back to roughly the source's own.
"""
import argparse
import os
import sys

from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E

PW, PH = 480, 300
PAD = 12
ROW_H = PH + 30


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--vpk', required=True)
    ap.add_argument('--model', required=True)
    ap.add_argument('--toc', required=True)
    ap.add_argument('--bonehash', required=True)
    ap.add_argument('--outdir', default='.')
    ap.add_argument('--out', required=True)
    ap.add_argument('--roll-mode', default='swing')
    ap.add_argument('--spine', default='mesh')
    ap.add_argument('--transfer', default='legs')
    ap.add_argument('--anchors', default='joint,neck,neck+spine')
    ap.add_argument('--zoom', action='store_true',
                    help='crop to the torso/head band instead of the whole body')
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

    cen = E.source_centroids(verts)
    names = {'Neck1', 'Head1'} | ({'Pelvis', 'Spine', 'Spine2', 'Spine4'}
                                  if 'spine' in a.anchors else set())
    anchor_set = {}
    for _b in E.read_source_bones(mdl):
        _nm = _b['name'][len(E.SRC_PREFIX):] if _b['name'].startswith(E.SRC_PREFIX) else None
        if _nm in names and _b['index'] in cen:
            anchor_set[_nm] = cen[_b['index']]

    def skinned(anchor):
        tr = E.LEG_BONES if a.transfer == 'legs' else ()
        _s, _h, _T, resolved, _d, _m, _sn = E.build_bone_map(
            mdl, a.toc, a.bonehash, a.roll_mode, transfer_bones=tr,
            spine_map=a.spine, anchor_centroids=anchor)
        return E.skin(verts, _s, resolved)

    rows = [('source bind (L4D2)', src_pts)]
    for tag in a.anchors.split(','):
        tag = tag.strip()
        if not tag:
            continue
        anc = {} if tag == 'joint' else (
            anchor_set if tag == 'neck+spine' else
            {k: v for k, v in anchor_set.items() if k in ('Neck1', 'Head1')})
        pts = skinned(anc)
        dev, _ang, _det = E.kabsch_residual(src_pts, pts)
        em, _p50, ep95, _p99, emax, ebad = E.edge_distortion(src_pts, pts, tris)
        sb, db = E.bbox(src_pts), E.bbox(pts)
        print('%-11s height %.3f -> %.3f m (%+.1f%%) | edge mean %.4f p95 %.4f '
              'max %.2f | bad>25%% %.2f%% | dev mean %.4f'
              % (tag, sb[5] - sb[2], db[5] - db[2],
                 100.0 * ((db[5] - db[2]) / (sb[5] - sb[2]) - 1.0),
                 em, ep95, emax, 100 * ebad, sum(dev) / len(dev)))
        rows.append(('anchor=%s' % tag, pts))

    W = PAD + 2 * (PW + PAD)
    H = 30 + len(rows) * ROW_H
    img = Image.new('RGB', (W, H), (28, 30, 36))
    draw = ImageDraw.Draw(img)
    draw.text((PAD, 8), 'anchor policy -- %s  (spine=%s transfer=%s roll=%s)'
              % (os.path.basename(mdl_path), a.spine, a.transfer, a.roll_mode),
              fill=(235, 235, 245))

    allp = [p for _l, pts in rows for p in pts]
    gb = list(E.bbox(allp))
    if a.zoom:
        span = gb[5] - gb[2]
        gb[2] = gb[2] + span * 0.45
        gb[5] = gb[2] + span * 0.57
    gb[0] = min(gb[0], -0.01)
    gb[3] = max(gb[3], 0.01)
    gb = tuple(gb)
    sc = min(PW, PH) * (1 - 2 * 0.06) / max(gb[3] - gb[0], gb[5] - gb[2], 0.001)

    for i, (label, pts) in enumerate(rows):
        y = 30 + i * ROW_H
        draw.text((PAD + 4, y - 14), label, fill=(200, 210, 225))
        E.render_panel(img, draw, pts, tris, gb, 0, 2, 1, False,
                       (PAD, y, PAD + PW, y + PH), (24, 26, 32), sc)
        draw.text((PAD + PW + PAD + 4, y - 14), label + '  (side)', fill=(200, 210, 225))
        E.render_panel(img, draw, pts, tris, gb, 1, 2, 0, True,
                       (PAD + PW + PAD, y, PAD + 2 * PW + PAD, y + PH),
                       (24, 26, 32), sc)

    os.makedirs(a.outdir, exist_ok=True)
    out = os.path.join(a.outdir, a.out)
    img.save(out)
    print('[written] %s' % out)


if __name__ == '__main__':
    main()
