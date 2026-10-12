"""Round 45d: a physical CRACK metric.

Two vertices that sat within 4 mm of each other in the un-rotated build belong to
neighbouring shells (bare arm / sleeve / cuff / frill).  If the arm rotation moves
them apart, the garment visibly splits.  This measures exactly that, for the three
arm_rot modes.

usage: python _dbg_crack.py
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
AR = (10.46, -18.55, 29.63)
BASE = dict(align='yaw', spine_map='mesh', fallback='region', leg_tilt=5.7,
            leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060), upper_scale=1.12,
            upper_pivot_z=1.15, upper_blend=0.45, arm_fit=1.0, verbose=False)


def run(**kw):
    b = dict(BASE); b.update(kw)
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, **b)


def main():
    off = run(arm_rot=None)
    P0 = np.asarray(off['out'], float)
    idx = {h['name']: h['index'] for h in off['hd2'] if h['name']}
    rest = off['rest_shipped']
    a = rest[idx['l_shoulder']][:3, 3].astype(float)
    b = rest[idx['l_hand']][:3, 3].astype(float)
    u = (b - a) / np.linalg.norm(b - a)
    t0 = (P0 - a) @ u
    perp0 = np.linalg.norm((P0 - a) - np.outer(t0, u), axis=1)
    region = (P0[:, 0] < -0.15) & (perp0 < 0.16) & (t0 > -0.05) & (t0 < 0.85)
    print('arm region: %d verts' % int(region.sum()))

    # close pairs inside the region (and not a mesh edge -- a crack is between
    # vertices that are NOT connected)
    tris = np.asarray([q[:3] for q in off['tris']])
    eset = set()
    for e in np.concatenate([tris[:, [0, 1]], tris[:, [1, 2]], tris[:, [2, 0]]]):
        eset.add((min(e[0], e[1]), max(e[0], e[1])))
    ids = np.where(region)[0]
    # voxel grid neighbour search (4 mm cells), no scipy
    cell = np.floor(P0[ids] / 0.004).astype(np.int64)
    buckets = {}
    for k, c in enumerate(map(tuple, cell)):
        buckets.setdefault(c, []).append(k)
    offs = [(dx, dy, dz) for dx in (-1, 0, 1) for dy in (-1, 0, 1)
            for dz in (-1, 0, 1)]
    seen = set()
    pl = []
    for c, members in buckets.items():
        near = []
        for o in offs:
            near.extend(buckets.get((c[0] + o[0], c[1] + o[1], c[2] + o[2]), ()))
        for k in members:
            for m in near:
                if m <= k:
                    continue
                i, j = ids[k], ids[m]
                key = (min(i, j), max(i, j))
                if key in seen or key in eset:
                    continue
                if np.linalg.norm(P0[i] - P0[j]) <= 0.004:
                    seen.add(key)
                    pl.append((i, j))
    pairs = np.array(pl, dtype=np.int64)
    d0 = np.linalg.norm(P0[pairs[:, 0]] - P0[pairs[:, 1]], axis=1)
    print('non-connected vertex pairs closer than 4 mm: %d' % len(pairs))

    print('\n%-26s %9s %9s %9s %9s' %
          ('', 'max(m)', 'p99.9(m)', '>5mm', '>10mm'))
    for tag, kw in (('no arm_rot', dict(arm_rot=None)),
                    ('blend (45c)', dict(arm_rot=AR, arm_rot_mode='blend')),
                    ('seq (LBS weights)', dict(arm_rot=AR, arm_rot_mode='seq')),
                    ('axis (rigid segs)', dict(arm_rot=AR, arm_rot_mode='axis'))):
        r = run(**kw)
        P = np.asarray(r['out'], float)
        d = np.linalg.norm(P[pairs[:, 0]] - P[pairs[:, 1]], axis=1)
        print('%-26s %9.4f %9.4f %9d %9d'
              % (tag, float(d.max()), float(np.percentile(d, 99.9)),
                 int((d > 0.005).sum()), int((d > 0.010).sum())))

    # where is the worst pair in the axis build?
    r = run(arm_rot=AR, arm_rot_mode='axis')
    P = np.asarray(r['out'], float)
    d = np.linalg.norm(P[pairs[:, 0]] - P[pairs[:, 1]], axis=1)
    o = np.argsort(-d)[:8]
    print('\nworst pairs (axis):')
    for k in o:
        i, j = pairs[k]
        mid = 0.5 * (P0[i] + P0[j])
        print('   %.4f m  orig %.4f m  at (%+.3f %+.3f %+.3f)'
              % (d[k], d0[k], mid[0], mid[1], mid[2]))


if __name__ == '__main__':
    main()
