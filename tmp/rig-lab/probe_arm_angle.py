"""Round 45c: the ARM ANGLE in each projection plane.

The user looked at arm-zoom-on.png and says the front view (X-Z) shows our arm
hanging ~14 deg too steeply, and the forearm bent the wrong way by ~10 deg, while
the top view (X-Y) is fine.  That is a claim about DIRECTION, so measure direction:

for bins along HD2's shoulder->hand axis take the mesh's centroid, then fit a line
through the bins of the upper arm (t in [0, elbow]) and of the forearm
(t in [elbow, wrist]) and report the angle in the X-Z plane and in the X-Y plane,
next to HD2's own bone directions.  Ours (weight split) and the reference
(nearest-bone split) are computed the same way.

usage: python probe_arm_angle.py [--arm-rot SH EL WR]
"""
import argparse
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
REF_ARM = 'out/patchdump/unit-91eb7be84016ceab.obj'
SEGS = [('upper arm', 'l_shoulder', 'l_elbow'),
        ('forearm', 'l_elbow', 'l_hand'),
        ('hand', 'l_hand', 'l_middle_finger1')]


def ang_xz(v):
    """degrees BELOW horizontal in the X-Z (front) plane, measured outward (-X)."""
    return float(np.degrees(np.arctan2(abs(v[2]), abs(v[0]))))


def ang_xy(v):
    """degrees FORWARD of the outward (-X) direction, in the X-Y (top) plane."""
    return float(np.degrees(np.arctan2(v[1], -v[0])))


def fit_dir(P, t, lo, hi, bins=8):
    """Least-squares direction of the bin-centroid path between t=lo..hi."""
    sel = (t >= lo) & (t < hi)
    if sel.sum() < 40:
        return None, 0
    tt = t[sel]
    pts = P[sel]
    edges = np.linspace(lo, hi, bins + 1)
    cs = []
    for i in range(bins):
        m = (tt >= edges[i]) & (tt < edges[i + 1])
        if m.sum() < 8:
            continue
        cs.append(pts[m].mean(0))
    if len(cs) < 3:
        return None, 0
    cs = np.asarray(cs)
    d = cs[-1] - cs[0]
    return d / max(np.linalg.norm(d), 1e-12), len(cs)


def arm_cloud(r):
    """(P, t) for the arm-dominant vertices of a retarget result."""
    idx = {h['name']: h['index'] for h in r['hd2']}
    rest = r['rest_shipped']
    out = np.asarray(r['out'], float)
    res = r['resolved']
    arm_idx = [h['index'] for h in r['hd2'] if H.is_arm_bone(h['name'])]
    dom = []
    for v in r['verts']:
        best, bw = None, -1.0
        for j in range(3):
            w = v['w'][j]
            if w <= bw:
                continue
            best, bw = res.get(v['bones'][j]), w
        dom.append(-1 if best is None else best)
    ours_arm = np.isin(np.array(dom), arm_idx)
    a = np.array(rest[idx['l_shoulder']][:3, 3], float)
    b = np.array(rest[idx['l_hand']][:3, 3], float)
    u = (b - a) / np.linalg.norm(b - a)
    P = out[ours_arm]
    return P, (P - a) @ u, a, u


def measure(r, quiet=False, tsel=None):
    """Print (and return) the per-segment direction table for one retarget result.

    `tsel` (optional) is the arm-axis coordinate of every arm vertex from a
    REFERENCE run (normally `arm_rot=None`).  Passing it freezes which vertices
    belong to "upper arm" / "forearm" / "hand", so two configurations are
    compared on the same vertices.  Without it the windows are recomputed from
    the current geometry, and a rotation slides vertices across a window edge --
    which made the numbers jump around and even change sign.
    """
    def p(*args):
        if not quiet:
            print(*args)

    idx = {h['name']: h['index'] for h in r['hd2']}
    rest = r['rest_shipped']

    def head(nm):
        return np.array(rest[idx[nm]][:3, 3], float)

    a, b = head('l_shoulder'), head('l_hand')
    u = (b - a) / np.linalg.norm(b - a)
    t_elbow = float((head('l_elbow') - a) @ u)
    t_wrist = float(np.linalg.norm(b - a))

    p('HD2 bones (rest_shipped), front X-Z and top X-Y:')
    for nm0, nm1 in (('l_shoulder', 'l_elbow'), ('l_elbow', 'l_hand'),
                     ('l_hand', 'l_middle_finger1')):
        d = head(nm1) - head(nm0)
        p('  %-16s -> %-20s  X-Z %6.2f deg below horiz | X-Y %+6.2f deg '
          'forward' % (nm0, nm1, ang_xz(d), ang_xy(d)))

    P, t, _, _ = arm_cloud(r)
    if tsel is not None:
        t = tsel
    ref_pts = np.asarray(PV.read_obj(REF_ARM)[0], float)
    ref_t = (ref_pts - a) @ u

    p('\nbin-centroid direction fits (upper arm t 0.02..%.3f, forearm %.3f..%.3f)'
      % (t_elbow, t_elbow, t_wrist))
    p('  %-10s %-10s %8s %8s' % ('cloud', 'segment', 'X-Z', 'X-Y'))
    M = {}
    for label, cloud, tt in (('ours', P, t), ('ref', ref_pts, ref_t)):
        M[label] = {}
        for sname, lo, hi in (('upper arm', 0.02, t_elbow),
                              ('forearm', t_elbow, t_wrist),
                              ('hand', t_wrist, t_wrist + 0.14)):
            d, n = fit_dir(cloud, tt, lo, hi)
            if d is None:
                p('  %-10s %-10s  (too few bins)' % (label, sname))
                continue
            M[label][sname] = (ang_xz(d), ang_xy(d))
            p('  %-10s %-10s %6.2f deg %+7.2f deg   (bins %d)'
              % (label, sname, ang_xz(d), ang_xy(d), n))

    # the elbow angle: between the upper-arm and forearm fitted directions
    p('\nelbow bend (angle between the two fitted segment directions):')
    M['elbow'] = {}
    for label, cloud, tt in (('ours', P, t), ('ref', ref_pts, ref_t)):
        d1, _ = fit_dir(cloud, tt, 0.02, t_elbow)
        d2, _ = fit_dir(cloud, tt, t_elbow, t_wrist)
        if d1 is None or d2 is None:
            continue
        c = abs(float(d1 @ d2))
        M['elbow'][label] = float(np.degrees(np.arccos(min(1.0, c))))
        p('  %-6s %.2f deg' % (label, M['elbow'][label]))
    d1 = head('l_elbow') - head('l_shoulder')
    d2 = head('l_hand') - head('l_elbow')
    M['elbow']['hd2'] = float(np.degrees(np.arccos(
        min(1.0, abs(float(d1 @ d2)) / np.linalg.norm(d1) / np.linalg.norm(d2)))))
    p('  HD2    %.2f deg' % M['elbow']['hd2'])
    return M


def run(arm_rot=None):
    """Retarget zoey with the given arm-rot and measure.  Used by the sweeps."""
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                      spine_map='mesh', fallback='region', leg_tilt=5.7,
                      leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                      upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                      arm_fit=1.0, verbose=False, arm_rot=arm_rot)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--arm-rot', type=float, nargs=3, default=None,
                    metavar=('SH', 'EL', 'WR'))
    ap.add_argument('--tag', default='')
    a = ap.parse_args()
    r = run(None if a.arm_rot is None else tuple(a.arm_rot))
    print('== arm-rot %s%s' % (
        'off' if a.arm_rot is None else ' '.join('%+.1f' % v for v in a.arm_rot),
        ('  [%s]' % a.tag) if a.tag else ''))
    measure(r)


if __name__ == '__main__':
    main()
