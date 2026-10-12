"""Round 45e step 1c: put the four centre-line polylines on ONE front view, so it
is obvious which ones are straight and which one is bent.

left panel : zoomed front view of the left forearm, with the slice-centroid
             polyline of each build drawn on top (same colours everywhere)
right panel: the same polylines as q(t), the lateral offset from the bone axis

usage: python probe_forearm_curve3.py
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import patch_view as PV
import export_obj as E

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
REF_OBJ = 'out/patchdump/unit-91eb7be84016ceab.obj'
ROT = (10.46, -18.55, 29.63)
T_LO, T_HI, T_STEP = 0.06, 0.58, 0.020
PERP = 0.130


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
    return a, u, idx


def centroid_poly(P, a, u, sel):
    t = (P - a) @ u
    u2 = np.array([u[0], u[2]]); u2 /= np.linalg.norm(u2)
    n2 = np.array([-u2[1], u2[0]])
    a2 = np.array([a[0], a[2]])
    e2 = np.stack([P[:, 0], P[:, 2]], 1) - a2
    s, q = e2 @ u2, e2 @ n2
    pts, tq, nq = [], [], []
    edges = np.arange(T_LO, T_HI + 1e-9, T_STEP)
    for i in range(len(edges) - 1):
        m = sel & (t >= edges[i]) & (t < edges[i + 1])
        if m.sum() < 12:
            pts.append(None); tq.append(0.5 * (edges[i] + edges[i + 1]))
            nq.append(np.nan)
            continue
        cs, cq = float(s[m].mean()), float(q[m].mean())
        w = a2 + cs * u2 + cq * n2
        pts.append((w[0], 0.0, w[1]))
        tq.append(0.5 * (edges[i] + edges[i + 1]))
        nq.append(cq)
    return pts, np.array(tq), np.array(nq)


def main():
    builds = [('no arm_rot', dict(arm_fit=1.0, arm_rot=None), (110, 190, 255)),
              ('ALL (current)', dict(arm_fit=1.0, arm_rot=ROT,
                                     arm_rot_mode='axis'), (255, 80, 80)),
              ('no elbow rot', dict(arm_fit=1.0, arm_rot=(ROT[0], 0.0, ROT[2]),
                                    arm_rot_mode='axis'), (255, 205, 70))]
    R = [(tag, run(**kw), col) for tag, kw, col in builds]
    base = R[0][1]
    P0 = np.asarray(base['out'], float)
    a, u, idx = frame(base)
    d0 = P0 - a
    t0 = d0 @ u
    perp0 = np.linalg.norm(d0 - np.outer(t0, u), axis=1)
    sel = (P0[:, 0] < -0.15) & (perp0 < PERP) & (t0 > T_LO - 0.04) & (t0 < T_HI + 0.04)
    print('slab verts %d' % sel.sum())

    series = []
    for tag, r, col in R:
        pts, tt, qq = centroid_poly(np.asarray(r['out'], float), a, u, sel)
        series.append((tag, col, pts, tt, qq))
    refP, _ = PV.read_obj(REF_OBJ)
    refP = np.asarray(refP, float)
    pts, tt, qq = centroid_poly(refP, a, u, refP[:, 0] < -0.15)
    series.append(('REFERENCE', (255, 150, 240), pts, tt, qq))

    for tag, col, pts, tt, qq in series:
        m = np.isfinite(qq)
        print('%-14s  q(t) span %7.1f mm   npts %d' %
              (tag, 1000 * (np.nanmax(qq) - np.nanmin(qq)), int(m.sum())))

    # ---- figure ----------------------------------------------------------
    W, Hh = 1500, 760
    img = Image.new('RGB', (W, Hh), (26, 28, 34))
    dr = ImageDraw.Draw(img)
    dr.text((12, 10), 'round 45e step 1c: the slice-centroid polyline of the LEFT '
            'forearm, all four on the same front view', fill=(232, 232, 244))
    dr.text((12, 28), 'the forearm polyline must be a STRAIGHT line.  blue = before '
            'arm_rot, red = current, yellow = current minus the elbow ramp, '
            'pink = REFERENCE', fill=(150, 160, 180))
    dr.text((12, 46), 'left: zoomed front view x[-0.76,-0.38] z[1.12,1.42]   |   '
            'right: q(t) = lateral offset from the shoulder->hand axis',
            fill=(150, 160, 180))

    box = [-0.76, -1.0, 1.12, -0.38, 1.0, 1.42]
    p = PV.Panel(img, dr, (12, 70, 12 + 470, 70 + 620), box, 0, 2, 1, False)
    for tag, col, pts, tt, qq in series:
        if tag == 'REFERENCE':
            rp, rt = PV.read_obj(REF_OBJ)
            p.mesh(rp, rt, (0.72, 0.55, 0.68), 2)
        else:
            rr = [r for t2, r, c in R if t2 == tag][0]
            p.mesh([tuple(z) for z in np.asarray(rr['out'], float)],
                   rr['tris'], (0.34, 0.38, 0.46), 2)
    rig = E.read_hd2_rig(TOC, BH)
    head = {h['name']: (h['world'][3], h['world'][7], h['world'][11])
            for h in rig if h['name']}
    for nm, par in (('l_shoulder', 'l_clavicle'), ('l_elbow', 'l_shoulder'),
                    ('l_hand', 'l_elbow')):
        if nm in head and par in head:
            p.line(head[par], head[nm], (60, 200, 220), 2)
    for nm in ('l_shoulder', 'l_elbow', 'l_hand'):
        if nm in head:
            p.dot(head[nm], (255, 230, 120), 5)
    for tag, col, pts, tt, qq in series:
        seq = [q for q in pts if q is not None]
        for i in range(len(seq) - 1):
            p.line(seq[i], seq[i + 1], col, 3)
        for q in seq:
            p.dot(q, col, 3)

    # right panel: q(t)
    x0, x1, y0, y1 = 560, 1460, 660, 110
    tmin, tmax = 0.0, 0.60
    qs = np.concatenate([q[np.isfinite(q)] for _, _, _, _, q in series])
    qmin, qmax = float(qs.min()) - 0.005, float(qs.max()) + 0.005
    PX = lambda t: x0 + (t - tmin) / (tmax - tmin) * (x1 - x0)
    PY = lambda q: y0 + (q - qmin) / (qmax - qmin) * (y1 - y0)
    dr.text((x0, 90), 'q(t) = lateral offset of the centroid from the shoulder->hand '
            'axis (front view)', fill=(215, 222, 235))
    for q in np.arange(np.ceil(qmin * 20) / 20, qmax, 0.02):
        dr.line([(x0, PY(q)), (x1, PY(q))], fill=(54, 58, 70), width=1)
        dr.text((x0 - 52, PY(q) - 7), '%+5.0f' % (1000 * q), fill=(150, 160, 180))
    for t in np.arange(0.0, 0.61, 0.05):
        dr.line([(PX(t), y0), (PX(t), y1)], fill=(48, 52, 62), width=1)
        dr.text((PX(t) - 14, y0 + 6), '%.2f' % t, fill=(150, 160, 180))
    for nm, tt2 in (('elbow', 0.286), ('wrist', 0.555)):
        dr.line([(PX(tt2), y0), (PX(tt2), y1)], fill=(120, 126, 145), width=1)
        dr.text((PX(tt2) + 4, y1 + 6), nm, fill=(190, 198, 215))
    dr.text((x0 - 60, (y0 + y1) / 2), 'mm', fill=(200, 210, 225))
    for tag, col, pts, tt, qq in series:
        m = np.isfinite(qq)
        seq = [(PX(tt[i]), PY(qq[i])) for i in range(len(tt)) if m[i]]
        for i in range(len(seq) - 1):
            dr.line([seq[i], seq[i + 1]], fill=col, width=3)
        for z in seq:
            dr.ellipse([z[0] - 3, z[1] - 3, z[0] + 3, z[1] + 3], fill=col)
    ly = 120
    for tag, col, pts, tt, qq in series:
        m = np.isfinite(qq)
        # curvature inside the forearm window only
        w = m & (tt >= 0.30) & (tt <= 0.52)
        flips = 0
        if w.sum() >= 3:
            d = np.diff(qq[w])
            flips = int((np.sign(d[1:]) * np.sign(d[:-1]) < 0).sum())
        dr.line([(x0 + 700, ly + 6), (x0 + 730, ly + 6)], fill=col, width=3)
        dr.text((x0 + 738, ly - 2), '%-14s flips %d' % (tag, flips),
                fill=(215, 222, 235))
        ly += 20
    out = os.path.abspath('out/forearm-centreline.png')
    img.save(out)
    print('[written] %s' % out)


if __name__ == '__main__':
    main()
