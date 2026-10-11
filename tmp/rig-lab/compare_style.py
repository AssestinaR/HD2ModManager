"""Side-by-side: source bind | old per-bone T = H*S^-1 (best params) | new BatchTool-style.

Row 1 = the L4D2 source bind pose (reference).
Row 2 = the new BatchTool-style pipeline (uniform scale + snapped bone heads +
        the fixed A-pose table).
Row 3 = the old pipeline, at its *best* setting found so far
        (`--spine-map mesh --transfer-bones legs --anchor neck --roll-mode swing`).

All three rows share one camera and one scale, so height and stance are directly
comparable -- that is the whole point: the old pipeline inflates the model and
splays the legs, the new one does neither.
"""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
import hd2_style as H

LEGS = ('L_Calf', 'L_Foot', 'R_Calf', 'R_Foot')


def foot_spread(P):
    bot = P[P[:, 2] < P[:, 2].min() + 0.06]
    return float(bot[:, 0].max() - bot[:, 0].min())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--vpk', required=True)
    ap.add_argument('--model', required=True)
    ap.add_argument('--toc', required=True)
    ap.add_argument('--bonehash', required=True)
    ap.add_argument('--pose-profile', required=True)
    ap.add_argument('--outdir', default='out')
    ap.add_argument('--out', required=True)
    ap.add_argument('--align', default='kabsch')
    ap.add_argument('--spine-map', default='legacy')
    ap.add_argument('--crop-z', default='')
    ap.add_argument('--json-out', default='')
    a = ap.parse_args()

    os.makedirs(a.outdir, exist_ok=True)

    # --- new pipeline ------------------------------------------------------
    r = H.retarget(a.vpk, a.model, a.toc, a.bonehash, a.pose_profile,
                   align=a.align, spine_map=a.spine_map)
    verts, tris, src = r['verts'], r['tris'], r['src']
    src_pts = [tuple(p) for p in r['src_pts']]
    new_pts = [tuple(p) for p in r['out']]

    # --- old pipeline, best params ----------------------------------------
    _mp, mdl, _v2, _t2, src_bones, short = H.load_source(a.vpk, a.model)
    assert len(src_bones) == len(src)
    cen = E.source_centroids(verts)
    anchor_set = {}
    for b in src_bones:
        nm = b['name'][len(E.SRC_PREFIX):] if b['name'].startswith(E.SRC_PREFIX) else None
        if nm in ('Neck1', 'Head1') and b['index'] in cen:
            anchor_set[nm] = cen[b['index']]
    _sb, _h, _T, resolved, _d, _m, _sn = E.build_bone_map(
        mdl, a.toc, a.bonehash, 'swing', transfer_bones=set(LEGS),
        spine_map='mesh', anchor_centroids=anchor_set)
    old_pts = E.skin(verts, src_bones, resolved)

    crop = tuple(float(x) for x in a.crop_z.split(',')) if a.crop_z else None
    png = os.path.join(a.outdir, a.out)
    E.render(png, src_pts, old_pts, tris,
             'row1 L4D2 source bind | row2 NEW BatchTool-style (uniform scale + snapped heads '
             '+ fixed A-pose table) | row3 OLD per-bone T=H*S^-1 (spine=mesh transfer=legs '
             'anchor=neck swing)   [%s]' % os.path.basename(r['mdl_path']),
             extra=('BatchTool-style', new_pts), crop_z=crop)
    print('[written] %s' % png)

    em, _p50, _p95, _p99, emax, ebad = E.edge_distortion(src_pts, old_pts, tris)
    ob, nb = E.bbox(old_pts), E.bbox(new_pts)
    print('OLD  edge mean %.4f max %.2f bad>25%% %.2f%%' % (em, emax, 100 * ebad))
    print('NEW  edge mean %.4f max %.2f bad>25%% %.2f%%'
          % (r['metrics']['edge_mean'], r['metrics']['edge_max'],
             100 * r['metrics']['bad']))
    print('OLD  bbox z [%.3f, %.3f]  height %.3f' % (ob[2], ob[5], ob[5] - ob[2]))
    print('NEW  bbox z [%.3f, %.3f]  height %.3f' % (nb[2], nb[5], nb[5] - nb[2]))

    import numpy as np
    print('foot spread (bottom 6 cm)   source %.3f | NEW %.3f | OLD %.3f'
          % (foot_spread(np.array(src_pts)), foot_spread(np.array(new_pts)),
             foot_spread(np.array(old_pts))))

    if a.json_out:
        with open(a.json_out, 'w', encoding='utf-8') as f:
            json.dump(dict(old=dict(edge_mean=em, edge_max=emax, bad=ebad,
                                    bbox=list(ob), height=ob[5] - ob[2]),
                           new=r['metrics']), f, indent=1, ensure_ascii=False)
        print('[written] %s' % a.json_out)


if __name__ == '__main__':
    main()
