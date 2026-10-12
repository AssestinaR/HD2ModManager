"""Round 45b: does the mesh's arm FOLLOW the HD2 bones, segment by segment?

The user's second complaint is about alignment, not length: "the forearm is still
parallel to the upper arm, and the palm is dragged a bit too high by the forearm".
This measures it instead of eyeballing it.

For each of the three arm segments we take the HD2 bone segment (shoulder->elbow,
elbow->hand, hand->middle_finger1) and ask how well the mesh tube sits on it:

  d_perp   mean perpendicular distance of the cloud from the bone segment's line
  d_up     the same offset, signed along "up" (world +Z, made perpendicular)
  ang      angle between the cloud's principal axis and the bone's direction
  ext      how far the cloud runs along the segment
  near%    fraction of the cloud within 8 cm of the segment's line

Ours: each vertex is assigned to the arm bone that dominates its skinning weights.
Reference: no weights in the .obj, so each vertex goes to its NEAREST arm bone
segment.  Both use the SAME HD2 rig, so any difference is the mesh's.

usage: python probe_arm_dir.py
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import patch_view as PV
import export_obj as E

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
REF_ARM = 'out/patchdump/unit-91eb7be84016ceab.obj'

SEGS = [('upper arm', 'l_shoulder', 'l_elbow'),
        ('forearm', 'l_elbow', 'l_hand'),
        ('hand', 'l_hand', 'l_middle_finger1')]


def pca(P):
    c = P.mean(0)
    X = P - c
    w, v = np.linalg.eigh(X.T @ X)
    return c, v[:, -1]


def ang(a, b):
    a = np.asarray(a, float)
    b = np.asarray(b, float)
    c = abs(float(a @ b) / max(np.linalg.norm(a) * np.linalg.norm(b), 1e-12))
    return float(np.degrees(np.arccos(min(1.0, c))))


def seg_metrics(label, P, p0, p1):
    u = p1 - p0
    L = float(np.linalg.norm(u))
    u = u / max(L, 1e-12)
    up = np.array([0.0, 0.0, 1.0])
    up = up - u * float(up @ u)
    up = up / max(float(np.linalg.norm(up)), 1e-12)
    fwd = np.cross(u, up)
    d = P - p0
    along = d @ u
    perp = d - np.outer(along, u)
    c, ax = pca(P)
    dc = c - p0
    dc_perp = dc - u * float(dc @ u)
    near = float((np.linalg.norm(perp, axis=1) < 0.08).mean())
    print('     %-6s %6d %8.4f %8.4f %8.4f %8.2f %8.3f %8.3f %7.1f%%'
          % (label, len(P), float(np.linalg.norm(perp, axis=1).mean()),
             float(dc_perp @ up), float(dc_perp @ fwd), ang(ax, u),
             float(along.min()), float(along.max()), 100.0 * near))
    return u, float(along.mean())


def main():
    arm_perp = float(sys.argv[1]) if len(sys.argv) > 1 else 0.0
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                   spine_map='mesh', fallback='region', leg_tilt=5.7,
                   leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                   upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                   arm_fit=1.0, arm_perp=arm_perp, verbose=False)
    hd2 = r['hd2']
    idx = {h['name']: h['index'] for h in hd2}
    rest = r['rest_shipped']
    out = np.asarray(r['out'], float)
    resolved = r['resolved']
    verts = r['verts']

    def head(nm):
        return np.array(rest[idx[nm]][:3, 3], float)

    # --- HD2's own bone chain, for reference -----------------------------
    print('HD2 bone chain (rest_shipped):')
    dirs = []
    for sname, b0, b1 in SEGS:
        v = head(b1) - head(b0)
        dirs.append(v / np.linalg.norm(v))
        print('  %-10s %-22s -> %-22s  len %.4f m' % (sname, b0, b1, np.linalg.norm(v)))
    print('  angle upper arm <-> forearm : %.2f deg' % ang(dirs[0], dirs[1]))
    print('  angle forearm  <-> hand      : %.2f deg' % ang(dirs[1], dirs[2]))
    print('  angle upper arm <-> hand     : %.2f deg' % ang(dirs[0], dirs[2]))

    # --- ours, by dominant weight ----------------------------------------
    dom = {}
    for k, v in enumerate(verts):
        best, bw = None, 0.0
        for j in range(3):
            w = float(v['w'][j])
            if w <= bw:
                continue
            h = resolved.get(v['bones'][j])
            if h is None:
                continue
            best, bw = h, w
        if best is not None:
            dom.setdefault(best, []).append(k)

    print('\nOURS (weights) vs the bone segments')
    print('     %-6s %6s %8s %8s %8s %8s %8s %8s %8s'
          % ('cloud', 'n', 'd_perp', 'd_up', 'd_fwd', 'ang', 't_lo', 't_hi', 'near'))
    for sname, b0, b1 in SEGS:
        ks = dom.get(idx[b0])
        if not ks:
            print('     %-6s  (no vertices)' % sname)
            continue
        seg_metrics(sname, out[ks], head(b0), head(b1))

    # --- reference, by nearest arm bone segment --------------------------
    ref_pts = np.asarray(PV.read_obj(REF_ARM)[0], float)
    segs = [(head(b0), head(b1)) for _, b0, b1 in SEGS]
    best = np.full(len(ref_pts), 1e9)
    owner = np.zeros(len(ref_pts), dtype=np.int64)
    for si, (p0, p1) in enumerate(segs):
        u = p1 - p0
        L = float(np.linalg.norm(u))
        u = u / max(L, 1e-12)
        a = (ref_pts - p0) @ u
        a = np.clip(a, 0.0, L)
        d = np.linalg.norm(ref_pts - (p0 + np.outer(a, u)), axis=1)
        m = d < best
        best[m] = d[m]
        owner[m] = si
    print('\nREFERENCE sleeve (nearest bone segment)')
    print('     %-6s %6s %8s %8s %8s %8s %8s %8s %8s'
          % ('cloud', 'n', 'd_perp', 'd_up', 'd_fwd', 'ang', 't_lo', 't_hi', 'near'))
    for si, (sname, b0, b1) in enumerate(SEGS):
        sel = ref_pts[owner == si]
        if len(sel) < 30:
            print('     %-6s  (only %d vertices)' % (sname, len(sel)))
            continue
        seg_metrics(sname, sel, head(b0), head(b1))


if __name__ == '__main__':
    main()
