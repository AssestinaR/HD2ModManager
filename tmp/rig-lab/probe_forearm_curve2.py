"""Round 45e step 1b: plot the forearm centre line as a DEVIATION FROM A STRAIGHT
LINE, which is the thing the user actually sees.

q(t) = lateral offset of the slice centroid, measured in the FRONT view plane.
Fit q(t) = A*s + B over the forearm window, then plot the residual.
A straight forearm -> residual flat at 0.  A forearm "bent at both ends" -> an arc.

usage: python probe_forearm_curve2.py
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import patch_view as PV

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
REF_OBJ = 'out/patchdump/unit-91eb7be84016ceab.obj'
ROT = (10.46, -18.55, 29.63)

T_LO, T_HI, T_STEP = 0.02, 0.60, 0.020
WIN_LO, WIN_HI = 0.28, 0.52          # the clean forearm window
PERP = 0.110


def run(**kw):
    base = dict(align='yaw', spine_map='mesh', fallback='region', leg_tilt=5.7,
                leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                verbose=False)
    base.update(kw)
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, **base)


def frame(r):
    idx = {h['name']: h['index'] for h in r['hd2'] if h['name']}
    rest = r['rest_shipped']
    a = rest[idx['l_shoulder']][:3, 3].astype(float)
    b = rest[idx['l_hand']][:3, 3].astype(float)
    u = (b - a) / np.linalg.norm(b - a)
    u2 = np.array([u[0], u[2]]); u2 /= np.linalg.norm(u2)
    n2 = np.array([-u2[1], u2[0]])
    return a, u, u2, n2


def curve(P, a, u, u2, n2, sel, stat='median'):
    t = (P - a) @ u
    e2 = np.stack([P[:, 0], P[:, 2]], 1) - np.array([a[0], a[2]])
    s = e2 @ u2
    q = e2 @ n2
    rows = []
    edges = np.arange(T_LO, T_HI + 1e-9, T_STEP)
    for i in range(len(edges) - 1):
        m = sel & (t >= edges[i]) & (t < edges[i + 1])
        if m.sum() < 8:
            rows.append((0.5 * (edges[i] + edges[i + 1]), 0, np.nan, np.nan))
            continue
        if stat == 'median':
            cc = np.median(np.stack([s[m], q[m]], 1), 0)
        elif stat == 'mid':
            cc = np.array([0.5 * (s[m].min() + s[m].max()),
                           0.5 * (q[m].min() + q[m].max())])
        else:
            cc = np.array([s[m].mean(), q[m].mean()])
        rows.append((0.5 * (edges[i] + edges[i + 1]), int(m.sum()),
                     float(cc[0]), float(cc[1])))
    return np.array(rows, float)


def residual(arr):
    m = (arr[:, 0] >= WIN_LO) & (arr[:, 0] <= WIN_HI) & np.isfinite(arr[:, 3])
    if m.sum() < 4:
        return arr[:, 0], np.full(len(arr), np.nan), np.nan, 0
    A = np.stack([arr[m, 2], np.ones(int(m.sum()))], 1)
    coef, *_ = np.linalg.lstsq(A, arr[m, 3], rcond=None)
    dev = arr[:, 3] - (coef[0] * arr[:, 2] + coef[1])
    dev[~np.isfinite(arr[:, 3])] = np.nan
    dq = np.diff(arr[m, 3])
    flips = int((np.sign(dq[1:]) * np.sign(dq[:-1]) < 0).sum())
    return arr[:, 0], dev, float(np.nanmax(np.abs(dev))), flips


def main():
    builds = [('no arm_rot', dict(arm_fit=1.0, arm_rot=None), (120, 200, 255)),
              ('ALL (current)', dict(arm_fit=1.0, arm_rot=ROT,
                                     arm_rot_mode='axis'), (255, 96, 96)),
              ('no elbow rot', dict(arm_fit=1.0, arm_rot=(ROT[0], 0.0, ROT[2]),
                                    arm_rot_mode='axis'), (255, 200, 80))]
    R = [(tag, run(**kw), col) for tag, kw, col in builds]
    base = R[0][1]
    P0 = np.asarray(base['out'], float)
    a, u, u2, n2 = frame(base)
    d0 = P0 - a
    t0 = d0 @ u
    perp0 = np.linalg.norm(d0 - np.outer(t0, u), axis=1)
    sel = (P0[:, 0] < -0.15) & (perp0 < PERP) & (t0 > T_LO - 0.05) & (t0 < T_HI + 0.05)
    print('slab verts %d / %d   (perp<%.3f)' % (sel.sum(), len(P0), PERP))

    series = []
    for tag, r, col in R:
        P = np.asarray(r['out'], float)
        arr = curve(P, a, u, u2, n2, sel, 'median')
        tt, dev, dmax, flips = residual(arr)
        series.append((tag, col, tt, dev, dmax, flips))
        print('%-16s devmax %6.2f mm   monotonic-flips %d' % (tag, 1000 * dmax, flips))

    refP, _ = PV.read_obj(REF_OBJ)
    refP = np.asarray(refP, float)
    selr = refP[:, 0] < -0.15
    arrR = curve(refP, a, u, u2, n2, selr, 'median')
    tt, dev, dmax, flips = residual(arrR)
    series.append(('REFERENCE', (255, 150, 240), tt, dev, dmax, flips))
    print('%-16s devmax %6.2f mm   monotonic-flips %d' % ('REFERENCE', 1000 * dmax, flips))

    print('\n%6s | %10s %10s %10s %10s   (deviation from a straight line, mm)'
          % ('t', 'no arm_rot', 'ALL', 'no elbow', 'REFERENCE'))
    for i in range(len(series[0][2])):
        t = series[0][2][i]
        row = '%6.3f |' % t
        for _, _, _, dev, _, _ in series:
            v = dev[i] if i < len(dev) else np.nan
            row += ' %10s' % ('   --' if not np.isfinite(v) else '%+10.1f' % (1000 * v))
        print(row)

    # ---- plot -------------------------------------------------------------
    W, Hh = 1000, 470
    img = Image.new('RGB', (W, Hh), (26, 28, 34))
    dr = ImageDraw.Draw(img)
    x0, x1, y0, y1 = 90, 960, 400, 70
    tmin, tmax, dmin, dmax = 0.0, 0.60, -40.0, 40.0

    def PX(t):
        return x0 + (t - tmin) / (tmax - tmin) * (x1 - x0)

    def PY(d):
        return y0 + (d - dmin) / (dmax - dmin) * (y1 - y0)

    dr.text((12, 10), 'round 45e step 1b: LEFT forearm centre line, deviation from '
            'a straight line (FRONT view, mm)', fill=(232, 232, 244))
    dr.text((12, 28), 'flat at 0 = the forearm is straight.  an arc = "a stick bent '
            'at both ends".  window t=%.2f..%.2f is the fitted forearm.' %
            (WIN_LO, WIN_HI), fill=(150, 160, 180))
    dr.text((12, 46), 'elbow t=0.286   wrist t=0.555   --  right of the grey band is '
            'the hand, left of it is the upper arm', fill=(150, 160, 180))

    dr.rectangle([PX(WIN_LO), y1, PX(WIN_HI), y0], fill=(38, 41, 50))
    for d in (-40, -30, -20, -10, 0, 10, 20, 30, 40):
        yy = PY(d)
        dr.line([(x0, yy), (x1, yy)], fill=(60, 64, 76), width=1)
        dr.text((x0 - 34, yy - 7), '%+d' % d, fill=(150, 160, 180))
    for t in np.arange(0.0, 0.61, 0.05):
        xx = PX(t)
        dr.line([(xx, y0), (xx, y1)], fill=(48, 52, 62), width=1)
        dr.text((xx - 12, y0 + 6), '%.2f' % t, fill=(150, 160, 180))
    dr.text((x0 - 60, (y0 + y1) / 2 - 8), 'mm', fill=(200, 210, 225))
    dr.line([(x0, PY(0)), (x1, PY(0))], fill=(200, 210, 225), width=1)

    for tag, col, tt, dev, dmaxv, flips in series:
        pts = [(PX(tt[i]), PY(1000 * dev[i])) for i in range(len(tt))
               if np.isfinite(dev[i]) and tmin <= tt[i] <= tmax]
        for i in range(len(pts) - 1):
            dr.line([pts[i], pts[i + 1]], fill=col, width=3)
        for q in pts:
            dr.ellipse([q[0] - 2, q[1] - 2, q[0] + 2, q[1] + 2], fill=col)
    ly = 96
    for tag, col, tt, dev, dmaxv, flips in series:
        dr.line([(x0 + 10, ly + 6), (x0 + 40, ly + 6)], fill=col, width=3)
        dr.text((x0 + 48, ly - 2), '%-14s  devmax %5.1f mm   flips %d'
                % (tag, 1000 * dmaxv, flips), fill=(215, 222, 235))
        ly += 20
    out = os.path.abspath('out/forearm-dev.png')
    img.save(out)
    print('\n[written] %s' % out)


if __name__ == '__main__':
    main()
