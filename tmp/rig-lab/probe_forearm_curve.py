"""Round 45e / step 1: is the FOREARM's centre line straight or wavy?

The user (front orthographic view) says: the reference arm's edge/centre line is
almost straight, while ours has a straight upper arm but a forearm that is always
"bent at both ends" (a stick bent at two places).  A human forearm cannot bend.

So measure exactly that, and nothing else:

  * slice the arm along the shoulder->hand axis u (the HD2 bone chain),
  * take the centroid of the vertices in each thin slice,
  * project the resulting polyline onto the FRONT view plane (X-Z) and read off
      q(t) = lateral offset from the axis, in mm
      psi(t) = tangent angle in that plane, in degrees
  * "wavy" = q(t) is not monotone / psi(t) is not constant.

Ablations separate which of the three ramps (shoulder / elbow / wrist) bends it.

usage: python probe_forearm_curve.py [out.png]
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
REF_UNITS = [('out/patchdump/unit-91eb7be84016ceab.obj', 'LeftArm_Undergarment_Slim')]
PANEL = (470, 520)
STEP = 1
ROT = (10.46, -18.55, 29.63)

T_LO, T_HI, T_STEP = 0.04, 0.82, 0.025
FORE_LO, FORE_HI = 0.300, 0.555          # "the forearm" for the summary numbers


def run(**kw):
    base = dict(align='yaw', spine_map='mesh', fallback='region', leg_tilt=5.7,
                leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                verbose=False)
    base.update(kw)
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, **base)


def axis_of(r, side='l'):
    idx = {h['name']: h['index'] for h in r['hd2'] if h['name']}
    rest = r['rest_shipped']
    a = rest[idx['%s_shoulder' % side]][:3, 3].astype(float)
    b = rest[idx['%s_hand' % side]][:3, 3].astype(float)
    u = (b - a) / np.linalg.norm(b - a)
    return a, u, idx


def profile(P, a, u, sel, side='l'):
    """centroid per slice, projected on the front (X-Z) plane."""
    sgn = 1.0 if side == 'l' else -1.0
    u2 = np.array([u[0], u[2]])
    u2 = u2 / max(float(np.linalg.norm(u2)), 1e-9)
    n2 = np.array([-u2[1], u2[0]])
    a2 = np.array([a[0], a[2]])
    # s/q measured in the X-Z plane; t measured in 3D along u
    t = (P - a) @ u
    e2 = np.stack([P[:, 0], P[:, 2]], 1) - a2
    s = e2 @ u2
    q = e2 @ n2
    out = []
    edges = np.arange(T_LO, T_HI + 1e-9, T_STEP)
    for i in range(len(edges) - 1):
        lo, hi = edges[i], edges[i + 1]
        m = sel & (t >= lo) & (t < hi)
        if m.sum() < 15:
            out.append((0.5 * (lo + hi), 0, np.nan, np.nan, np.nan))
            continue
        c2 = np.array([s[m].mean(), q[m].mean()])
        out.append((0.5 * (lo + hi), int(m.sum()), float(c2[0]),
                    float(c2[1]), 0.0))
    arr = np.array(out, float)
    return arr, t, s, q, u2, n2


def waviness(arr, lo, hi):
    """how far from a straight line is the centroid polyline inside [lo,hi]?"""
    m = (arr[:, 0] >= lo) & (arr[:, 0] <= hi) & np.isfinite(arr[:, 3])
    if m.sum() < 4:
        return dict(n=0, qrange=np.nan, signflips=0, psirange=np.nan,
                    devmax=np.nan, chord=np.nan)
    tt = arr[m, 0]
    qq = arr[m, 3]
    ss = arr[m, 2]
    # fit a straight line q(s) over the window, look at the residual
    A = np.stack([ss, np.ones_like(ss)], 1)
    coef, *_ = np.linalg.lstsq(A, qq, rcond=None)
    dev = qq - A @ coef
    dq = np.diff(qq)
    flips = int((np.sign(dq[1:]) * np.sign(dq[:-1]) < 0).sum())
    # tangent angle in the front plane, in degrees
    ds = np.diff(ss)
    dq2 = np.diff(qq)
    psi = np.degrees(np.arctan2(dq2, np.maximum(np.abs(ds), 1e-9)))
    return dict(n=int(m.sum()), qrange=float(qq.max() - qq.min()),
                signflips=flips,
                psirange=float(psi.max() - psi.min()) if len(psi) else 0.0,
                devmax=float(np.abs(dev).max()),
                chord=float(np.linalg.norm([ss[-1] - ss[0], qq[-1] - qq[0]])))


def main():
    out = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else 'out/forearm-curve.png')
    builds = [
        ('no arm_rot', dict(arm_fit=1.0, arm_rot=None)),
        ('ALL 10.46 -18.55 29.6', dict(arm_fit=1.0, arm_rot=ROT,
                                       arm_rot_mode='axis')),
        ('no shoulder rot', dict(arm_fit=1.0, arm_rot=(0.0, ROT[1], ROT[2]),
                                 arm_rot_mode='axis')),
        ('no elbow rot', dict(arm_fit=1.0, arm_rot=(ROT[0], 0.0, ROT[2]),
                              arm_rot_mode='axis')),
        ('no wrist rot', dict(arm_fit=1.0, arm_rot=(ROT[0], ROT[1], 0.0),
                              arm_rot_mode='axis')),
    ]
    print('running %d builds ...' % len(builds))
    R = [(tag, run(**kw)) for tag, kw in builds]
    base = R[0][1]
    P0 = np.asarray(base['out'], float)
    a, u, idx = axis_of(base)
    d0 = P0 - a
    t0 = d0 @ u
    perp0 = np.linalg.norm(d0 - np.outer(t0, u), axis=1)
    sel = (P0[:, 0] < -0.15) & (perp0 < 0.140) & (t0 > T_LO - 0.05) & (t0 < T_HI + 0.05)
    print('left-arm slab vertices: %d of %d' % (int(sel.sum()), len(P0)))
    print('elbow t=0.286   wrist t=0.555   fingertip t~0.750\n')

    ref = [(nm,) + PV.read_obj(p) for p, nm in REF_UNITS]
    refP = np.concatenate([np.asarray(rp, float) for _, rp, _ in ref])

    hdr = '%-24s %8s %8s %8s %8s' % ('build', 'q range', 'flips', 'psi rng', 'devmax')
    print(hdr + '   (mm / count / deg / mm, over t=%.2f..%.2f)' % (FORE_LO, FORE_HI))
    print('-' * len(hdr))
    prof = []
    for tag, r in R:
        P = np.asarray(r['out'], float)
        arr, *_ = profile(P, a, u, sel)
        w = waviness(arr, FORE_LO, FORE_HI)
        print('%-24s %8.1f %8d %8.1f %8.1f'
              % (tag, 1000 * w['qrange'], w['signflips'], w['psirange'],
                 1000 * w['devmax']))
        prof.append((tag, r, arr))
    # reference: no rig, use the same axis
    selr = (refP[:, 0] < -0.15)
    arrR, *_ = profile(refP, a, u, selr)
    wR = waviness(arrR, FORE_LO, FORE_HI)
    print('%-24s %8.1f %8d %8.1f %8.1f'
          % ('REFERENCE', 1000 * wR['qrange'], wR['signflips'], wR['psirange'],
             1000 * wR['devmax']))
    prof.append(('REFERENCE', None, arrR))

    # ---- per-slice dump for the two interesting builds ---------------------
    print('\nper-slice lateral offset q (mm), measured in the FRONT X-Z plane:')
    print('%6s %6s | %9s %9s | %9s %9s' %
          ('t', 'n', 'no arm_rot', 'ALL', 'no elbow', 'REFERENCE'))
    A = {tag: arr for tag, _, arr in prof}
    for i in range(len(A['no arm_rot'])):
        t = A['no arm_rot'][i, 0]
        row = '%6.3f %6d |' % (t, int(A['no arm_rot'][i, 1]))
        for tag in ('no arm_rot', 'ALL 10.46 -18.55 29.6', 'no elbow rot',
                    'REFERENCE'):
            v = A[tag][i, 3]
            row += ' %9s' % ('   --' if not np.isfinite(v) else '%+9.1f' % (1000 * v))
        print(row)

    # ---- figure -----------------------------------------------------------
    rig = E.read_hd2_rig(TOC, BH)
    head = {h['name']: (h['world'][3], h['world'][7], h['world'][11])
            for h in rig if h['name']}
    ed = []
    for h in rig:
        p = h['parent']
        if 0 <= p < len(rig) and h['name'] and rig[p]['name']:
            ed.append((head[rig[p]['name']], head[h['name']]))

    box = [-0.82, -1.10, 1.02, -0.14, 1.06, 1.62]
    cols = [(tag, r, arr) for tag, r, arr in prof]
    pw, ph = PANEL
    W = 12 + len(cols) * (pw + 12)
    Hh = 92 + ph + 16
    img = Image.new('RGB', (W, Hh), (26, 28, 34))
    dr = ImageDraw.Draw(img)
    dr.text((12, 8), 'round 45e step 1: LEFT forearm centre line, FRONT view '
            '(X-Z).  red = slice centroid polyline', fill=(232, 232, 244))
    dr.text((12, 26), 'cyan = HD2 shoulder->elbow->hand bone chain, yellow dots = '
            'its joints.  t=0.286 elbow, t=0.555 wrist.', fill=(150, 160, 180))
    dr.text((12, 44), 'box  x[-0.82,-0.14]  z[1.02,1.62]   --  the centroid '
            'polyline should be a STRAIGHT line for the forearm', fill=(150, 160, 180))
    dr.text((12, 62), 'ablations: with/without each of the three ramps, so the '
            'bend can be attributed.', fill=(150, 160, 180))
    for ci, (tag, r, arr) in enumerate(cols):
        cx = 12 + ci * (pw + 12)
        dr.text((cx + 4, 80), tag, fill=(205, 214, 228))
        p = PV.Panel(img, dr, (cx, 96, cx + pw, 96 + ph), box, 0, 2, 1, False)
        if r is None:
            for _, rp, rt in ref:
                p.mesh(rp, rt, (1.0, 0.86, 0.96), STEP)
        else:
            p.mesh([tuple(q) for q in np.asarray(r['out'], float)],
                   r['tris'], (0.78, 0.97, 0.84), STEP)
        for x, y in ed:
            p.line(x, y, (72, 77, 92), 1)
        for nm, par in (('l_clavicle', None), ('l_shoulder', 'l_clavicle'),
                        ('l_elbow', 'l_shoulder'), ('l_hand', 'l_elbow')):
            if nm in head and par in head:
                p.line(head[par], head[nm], (90, 220, 240), 2)
        # the centroid polyline: reconstruct the 3D points from (s,q) in X-Z
        u2 = np.array([u[0], u[2]]); u2 /= max(float(np.linalg.norm(u2)), 1e-9)
        n2 = np.array([-u2[1], u2[0]])
        a2 = np.array([a[0], a[2]])
        pts = []
        for k in range(len(arr)):
            if not np.isfinite(arr[k, 3]):
                continue
            e2 = a2 + arr[k, 2] * u2 + arr[k, 3] * n2
            pts.append((e2[0], 0.0, e2[1]))
        for k in range(len(pts) - 1):
            p.line(pts[k], pts[k + 1], (255, 96, 96), 2)
        for q in pts:
            p.dot(q, (255, 190, 90), 2)
        for nm in ('l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand'):
            if nm in head:
                p.dot(head[nm], (255, 210, 90), 4)
    img.save(out)
    print('\n[written] %s  %dx%d' % (out, W, Hh))


if __name__ == '__main__':
    main()
