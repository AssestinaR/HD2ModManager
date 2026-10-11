"""Render a before/after rig-map comparison.

Row 1: the original L4D2 bind pose.
Row 2: the OLD mapping (spine -> HD2 spine1, every joint snapped) -- the one the
       user reported as 长脖子 / 肩部压扁 / 脚位置不正确.
Row 3: the NEW mapping (spine -> HD2 spine2 by mesh centroid, sub-thigh chain
       keeps its own offsets, and optionally the mesh CENTROID anchored on the
       HD2 joint for Neck1/Head1 instead of the joint).

Both rows share one global scale, so the two mappings are directly comparable.
"""
import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--vpk', required=True)
    ap.add_argument('--model', required=True)
    ap.add_argument('--toc', required=True)
    ap.add_argument('--bonehash', required=True)
    ap.add_argument('--outdir', default='.')
    ap.add_argument('--out', required=True)
    ap.add_argument('--roll-mode', default='swing')
    ap.add_argument('--old-spine', default='legacy')
    ap.add_argument('--new-spine', default='mesh')
    ap.add_argument('--new-transfer', default='legs')
    ap.add_argument('--new-anchor', default='joint',
                    choices=('joint', 'neck', 'neck+spine', 'all'))
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
    print('mesh: %d verts / %d tris' % (len(verts), len(tris)))

    src_pts = [(v['pos'][0] * E.IN2M, v['pos'][1] * E.IN2M, v['pos'][2] * E.IN2M)
               for v in verts]

    # ---- centroid anchor table (fix (2): the "long neck") -------------------
    anchor_names = set()
    if a.new_anchor == 'neck':
        anchor_names = {'Neck1', 'Head1'}
    elif a.new_anchor == 'neck+spine':
        anchor_names = {'Pelvis', 'Spine', 'Spine2', 'Spine4', 'Neck1', 'Head1'}
    elif a.new_anchor == 'all':
        anchor_names = {n for n, _h in E.body_pairs(a.new_spine) + E.FINGERS + E.TOES}
    anchor_set = {}
    if anchor_names:
        cen = E.source_centroids(verts)
        for _b in E.read_source_bones(mdl):
            _nm = _b['name'][len(E.SRC_PREFIX):] if _b['name'].startswith(E.SRC_PREFIX) else None
            if _nm in anchor_names and _b['index'] in cen:
                anchor_set[_nm] = cen[_b['index']]
        print('anchor centroid: %s' % ', '.join(sorted(anchor_set)))

    def skinned(spine, transfer, anchor=None):
        tr = E.LEG_BONES if transfer == 'legs' else ()
        _s, _h, _T, resolved, _d, _m, _sn = E.build_bone_map(
            mdl, a.toc, a.bonehash, a.roll_mode, transfer_bones=tr,
            spine_map=spine, anchor_centroids=anchor)
        return E.skin(verts, _s, resolved)

    old_pts = skinned(a.old_spine, 'none')
    new_pts = skinned(a.new_spine, a.new_transfer, anchor_set)

    for label, pts in (('old', old_pts), ('new', new_pts)):
        dev, ang, det = E.kabsch_residual(src_pts, pts)
        em, ep50, ep95, ep99, emax, ebad = E.edge_distortion(src_pts, pts, tris)
        sb, db = E.bbox(src_pts), E.bbox(pts)
        print('%-4s height %.3f -> %.3f m | edge mean %.4f p95 %.4f max %.2f | '
              'bad>25%% %.2f%% | dev mean %.4f max %.4f'
              % (label, sb[5] - sb[2], db[5] - db[2], em, ep95, emax, 100 * ebad,
                 sum(dev) / len(dev), max(dev)))

    os.makedirs(a.outdir, exist_ok=True)
    png = os.path.join(a.outdir, a.out)
    E.render(png, src_pts, new_pts, tris,
             'A3 before/after -- row1 L4D2 bind | row2 OLD (spine->spine1, snap all) | '
             'row3 NEW (spine->spine2, transfer legs, anchor %s)   (%s)'
             % (a.new_anchor, os.path.basename(mdl_path)),
             extra=('OLD map', old_pts))
    print('[written] %s' % png)

    # Same comparison, zoomed on the torso/head band where the spine fix lands.
    sb = E.bbox(src_pts)
    zlo = sb[2] + (sb[5] - sb[2]) * 0.45
    zhi = sb[2] + (sb[5] - sb[2]) * 1.02
    zoom = os.path.join(a.outdir, a.out.replace('.png', '-zoom.png'))
    E.render(zoom, src_pts, new_pts, tris,
             'ZOOM torso/head -- row1 L4D2 bind | row2 OLD | row3 NEW (anchor %s)   (%s)'
             % (a.new_anchor, os.path.basename(mdl_path)),
             extra=('OLD map', old_pts), crop_z=(zlo, zhi))
    print('[written] %s' % zoom)


if __name__ == '__main__':
    main()
