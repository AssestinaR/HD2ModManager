"""Side-view silhouette profile: OURS(out_idle) vs the reference patch mod.

The user judged the BIND pose (A-pose) screenshots, but the game never shows that
frame -- it shows `out_idle` (= our mesh driven by HD2's own rest). This probe
measures the thing the user actually sees, per vertical band, near the midline
(so arms / skirt do not pollute the torso silhouette).

HD2 convention (measured): +Y = forward, +X = left, +Z = up.
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H

TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
BONEHASH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
POSE = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
MODEL = 'survivor_teenangst'
PATCH = 'out/patchdump'
PATCH_UNITS = ['cae35ee15168145f', '17da843a4908511e',
               '91eb7be84016ceab', '7336c6b662522f0e']


def read_obj(path):
    pts, tris = [], []
    with open(path, encoding='utf-8') as f:
        for ln in f:
            if ln.startswith('v '):
                a = ln.split()
                pts.append((float(a[1]), float(a[2]), float(a[3])))
            elif ln.startswith('f '):
                idx = [int(x.split('/')[0]) - 1 for x in ln.split()[1:]]
                for i in range(1, len(idx) - 1):
                    tris.append((idx[0], idx[i], idx[i + 1]))
    return np.asarray(pts), tris


def profile(P, tag, half=0.10):
    """Print, per 10 cm z band, the front/back of the midline silhouette."""
    print('\n%-28s   z-band    back(+Y min)  front(+Y max)   mid      width' % tag)
    z0 = P[:, 2].min()
    z1 = P[:, 2].max()
    for lo in np.arange(0.0, z1, 0.10):
        hi = lo + 0.10
        m = P[(P[:, 2] >= lo) & (P[:, 2] < hi) & (np.abs(P[:, 0]) < half)]
        if len(m) < 20:
            m = P[(P[:, 2] >= lo) & (P[:, 2] < hi) & (np.abs(P[:, 0]) < 0.18)]
        if len(m) < 20:
            continue
        ymin, ymax = float(m[:, 1].min()), float(m[:, 1].max())
        print('%-28s  %5.2f-%5.2f   %+8.4f     %+8.4f   %+8.4f   %.4f'
              % ('', lo, hi, ymin, ymax, (ymin + ymax) / 2, ymax - ymin))


def foot(P, tag, band=0.10):
    zmin = P[:, 2].min()
    m = P[P[:, 2] < zmin + band]
    ymin, ymax = float(m[:, 1].min()), float(m[:, 1].max())
    print('%-28s  FOOT  heel %+0.4f  toe %+0.4f  mid %+0.4f  len %.4f  z0 %+0.4f'
          % (tag, ymin, ymax, (ymin + ymax) / 2, ymax - ymin, zmin))
    return ymin, ymax, (ymin + ymax) / 2


def main():
    runs = {}
    for align in ('kabsch', 'yaw'):
        r = H.retarget(VPK, MODEL, TOC, BONEHASH, POSE,
                       align=align, spine_map='mesh', fallback='region',
                       verbose=False)
        runs[align] = r
        print('[%s] edge mean %.4f max %.2f bad %.2f%%  height %.3f'
              % (align, r['metrics']['edge_mean'], r['metrics']['edge_max'],
                 100 * r['metrics']['bad'], r['metrics']['height_after']))

    pts, tris, base = [], [], 0
    for fid in PATCH_UNITS:
        p, t = read_obj(os.path.join(PATCH, 'unit-%s.obj' % fid))
        if len(p):
            pts.extend(p)
            tris.extend([(a + base, b + base, c + base) for a, b, c in t])
            base += len(p)
    ref = np.asarray(pts)
    print('[patch] %d verts / %d tris  z[%.4f, %.4f]'
          % (len(ref), len(tris), ref[:, 2].min(), ref[:, 2].max()))

    for align in ('kabsch', 'yaw'):
        P = np.asarray(runs[align]['out_idle'])
        print('\n===== OURS out_idle (%s)  z[%.4f, %.4f] ====='
              % (align, P[:, 2].min(), P[:, 2].max()))
        profile(P, 'OURS-%s idle' % align)
        foot(P, 'OURS-%s idle' % align)

    print('\n===== reference patch mod  z[%.4f, %.4f] ====='
          % (ref[:, 2].min(), ref[:, 2].max()))
    profile(ref, 'REFERENCE patch')
    foot(ref, 'REFERENCE patch')

    # also: bind pose (what the user was looking at)
    for align in ('kabsch', 'yaw'):
        P = np.asarray(runs[align]['out'])
        print('\n===== OURS bind pose (%s)  z[%.4f, %.4f] ====='
              % (align, P[:, 2].min(), P[:, 2].max()))
        foot(P, 'OURS-%s bind' % align)


if __name__ == '__main__':
    main()
