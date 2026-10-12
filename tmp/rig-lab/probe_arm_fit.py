"""Where is our ARM mesh relative to HD2's own arm bones?

Round 44 settled the skirt/hair.  Round 45 is the arm: the user reports (a) the
front view shows our arm bent where the reference's is a straight diagonal, and
(b) the top view shows our upper arm too short, so the forearm/hand sit inboard
of HD2's elbow/hand bones.

This probe measures exactly that, in the space the player sees:

  * our mesh  = `r['out']`  (native mode -> out_idle == out, so this IS the frame
    the game displays)
  * HD2 bones = `r['rest_hd2']` (= `rest_shipped`; native mode ships them verbatim)

For every HD2 arm bone it takes the centroid of the vertices whose DOMINANT
weight is that bone, then compares the mesh polyline (clavicle->shoulder->elbow
->hand) with the bone polyline.  Lengths are compared segment by segment, and the
bend is reported as the angle at the elbow plus the overall shoulder->hand
direction, both in 3D and in the TOP view (x,y) -- top view is what the user was
looking at.
"""
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import patch_view as PV

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')

CHAIN = ['{s}_clavicle', '{s}_shoulder', '{s}_elbow', '{s}_hand']


def ang3(u, v):
    u, v = np.asarray(u, float), np.asarray(v, float)
    n = np.linalg.norm(u) * np.linalg.norm(v)
    return math.degrees(math.acos(np.clip(float(u @ v) / max(n, 1e-12), -1, 1)))


def top(u):
    """direction angle in the TOP view (x,y), 0 = +x, ccw."""
    return math.degrees(math.atan2(float(u[1]), float(u[0])))


def centroids(r):
    """centroid of vertices whose dominant resolved HD2 bone is `name`."""
    res, verts, out = r['resolved'], r['verts'], r['out']
    by_idx_name = {h['index']: h['name'] for h in r['hd2'] if h['name']}
    acc, cnt = {}, {}
    for k, v in enumerate(verts):
        best, bw = None, -1.0
        for j in range(3):
            w = v['w'][j]
            if w <= bw:
                continue
            hb = res.get(v['bones'][j])
            if hb is None:
                continue
            bw, best = w, hb
        if best is None:
            continue
        nm = by_idx_name.get(best)
        if nm is None:
            continue
        acc.setdefault(nm, np.zeros(3))
        acc[nm] += out[k]
        cnt[nm] = cnt.get(nm, 0) + 1
    return {nm: (acc[nm] / cnt[nm], cnt[nm]) for nm in acc}


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                   spine_map='mesh', fallback='region', leg_tilt=5.7,
                   leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                   upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                   verbose=False)
    cen = centroids(r)
    head = {h['name']: r['rest_hd2'][h['index']][:3, 3].astype(float)
            for h in r['hd2'] if h['name']}

    for side in ('l', 'r'):
        names = [c.format(s=side) for c in CHAIN]
        print('\n================ %s arm ================' % side.upper())
        print('%-14s | %-24s | %-24s | %7s' %
              ('bone', 'mesh centroid (x,y,z)', 'HD2 bone head (x,y,z)', 'gap'))
        for nm in names:
            if nm not in cen or nm not in head:
                print('%-14s | MISSING' % nm)
                continue
            c, n = cen[nm]
            h = head[nm]
            print('%-14s | %+7.4f %+7.4f %+7.4f | %+7.4f %+7.4f %+7.4f | %7.4f  (%d v)'
                  % (nm, c[0], c[1], c[2], h[0], h[1], h[2],
                     float(np.linalg.norm(c - h)), n))

        have = [nm for nm in names if nm in cen and nm in head]
        print('\nsegments (mesh centroid polyline  vs  HD2 bone polyline):')
        for a, b in zip(have, have[1:]):
            ma, mb = cen[a][0], cen[b][0]
            ha, hb = head[a], head[b]
            vm, vb = mb - ma, hb - ha
            print('  %-22s len %6.4f / %6.4f  (mesh/bone = %.3f)  '
                  '3D dir %5.1f  top %+7.1f / %+7.1f'
                  % ('%s->%s' % (a.split('_', 1)[1], b.split('_', 1)[1]),
                     float(np.linalg.norm(vm)), float(np.linalg.norm(vb)),
                     float(np.linalg.norm(vm)) / max(float(np.linalg.norm(vb)), 1e-9),
                     ang3(vm, vb), top(vm), top(vb)))

        # bend at the elbow + overall shoulder->hand direction
        if all(nm in cen for nm in names) and all(nm in head for nm in names):
            sh, el, hd = names[1], names[2], names[3]
            m1, m2 = cen[el][0] - cen[sh][0], cen[hd][0] - cen[el][0]
            b1, b2 = head[el] - head[sh], head[hd] - head[el]
            m0, b0 = cen[hd][0] - cen[sh][0], head[hd] - head[sh]
            print('\n  elbow bend (mesh %.1f deg / bone %.1f deg)  -> excess %+.1f deg'
                  % (ang3(m1, m2), ang3(b1, b2), ang3(m1, m2) - ang3(b1, b2)))
            print('  shoulder->hand  mesh |%.4f| top %+7.1f  |  bone |%.4f| top %+7.1f'
                  '  |  dir gap %.1f deg (3D), %+.1f deg (top)'
                  % (float(np.linalg.norm(m0)), top(m0),
                     float(np.linalg.norm(b0)), top(b0),
                     ang3(m0, b0), top(m0) - top(b0)))

        # a global shift of the whole arm chain (what the user suggested:
        # "translate the forearm outward a bit")
        both = [nm for nm in names if nm in cen and nm in head]
        Dv = np.array([cen[nm][0] - head[nm] for nm in both])
        print('  chain mean offset (%+.4f, %+.4f, %+.4f) |mean| %.4f | after removing '
              'it, max %.4f'
              % (Dv[:, 0].mean(), Dv[:, 1].mean(), Dv[:, 2].mean(),
                 float(np.linalg.norm(Dv.mean(0))),
                 float(np.linalg.norm(Dv - Dv.mean(0), axis=1).max())))

    # reference sleeve, for scale: where does ITS geometry sit on the same bones?
    print('\n================ reference sleeve (Slim) ================')
    try:
        import json
        meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
        for fid, tag in (('91eb7be84016ceab', 'LeftArm'),
                         ('7336c6b662522f0e', 'RightArm')):
            p, t = PV.read_obj('out/patchdump/unit-%s.obj' % fid)
            P = np.asarray(p, float)
            print('  %-9s %6d verts  z[%+.4f,%+.4f]  x[%+.4f,%+.4f]  y[%+.4f,%+.4f]'
                  % (tag, len(P), P[:, 2].min(), P[:, 2].max(),
                     P[:, 0].min(), P[:, 0].max(), P[:, 1].min(), P[:, 1].max()))
    except Exception as exc:                                  # noqa: BLE001
        print('  (skipped: %s)' % exc)


if __name__ == '__main__':
    main()
