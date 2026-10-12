"""Round 45d: WHERE exactly is the forearm/wrist/hand damage coming from?

The user reports that after round 45c the hand lines up but the FOREARM is twisted
into a weird shape, the sleeve CRACKS open from the forearm down, the tiger mouth
(thumb/index web) tears, and the wristband detaches.

This probe renders a tight zoom on the LEFT forearm + wrist + hand for five builds
side by side, and prints, for each, where the mesh is being torn apart:

    edge stretch ratio  = |edge| / |original edge|      (per triangle, max side)
    "seam" edges        = edges whose two ends are dominated by DIFFERENT arm
                          segments (shoulder / elbow / hand) -- the wrist and elbow
                          seams that open when a rotation is applied unevenly.

usage: python probe_arm_wrist.py [out.png]
"""
import os
import sys
from collections import Counter

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
PANEL = (520, 470)
STEP = 1
ARM_ROT = (10.46, -18.55, 29.63)
ARM_ROT_45C = (9.90, -17.25, 21.50)


def run(**kw):
    base = dict(align='yaw', spine_map='mesh', fallback='region', leg_tilt=5.7,
                leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                verbose=False)
    base.update(kw)
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, **base)


def seg_group(nm):
    """mirror hd2_style's 4e-2 grouping: 0 shoulder / 1 elbow / 2 hand."""
    if not nm or not H.is_arm_bone(nm) or nm[0] not in 'lr' or nm[1] != '_':
        return None
    tail = nm[2:]
    if 'finger' in tail or tail.startswith('hand'):
        return 2
    if tail.startswith('elbow'):
        return 1
    return 0


def main():
    out = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else 'out/arm-wrist.png')
    builds = [('no arm_rot (45b)', dict(arm_fit=1.0, arm_rot=None)),
              ('45c BLEND 9.9 -17.25 21.5', dict(arm_fit=1.0, arm_rot=ARM_ROT_45C,
                                                 arm_rot_mode='blend')),
              ('45c SEQ   9.9 -17.25 21.5', dict(arm_fit=1.0, arm_rot=ARM_ROT_45C,
                                                 arm_rot_mode='seq')),
              ('45d AXIS 10.46 -18.55 29.6', dict(arm_fit=1.0, arm_rot=ARM_ROT,
                                                  arm_rot_mode='axis')),
              ('45d AXIS no wrist rot    ', dict(arm_fit=1.0,
                                                 arm_rot=(10.46, -18.55, 0.0),
                                                 arm_rot_mode='axis'))]
    R = [(tag, run(**kw)) for tag, kw in builds]
    P0 = np.asarray(R[0][1]['out'], float)          # the no-arm-work baseline
    tris = np.asarray([t[:3] for t in R[0][1]['tris']])
    verts = R[0][1]['verts']
    res = R[0][1]['resolved']
    by_idx = {h['index']: h['name'] for h in R[0][1]['hd2'] if h['name']}

    # per-vertex dominant bone + its arm segment
    dom = []
    for v in verts:
        best, bw = None, -1.0
        for j in range(3):
            w = v['w'][j]
            if w <= bw:
                continue
            best, bw = res.get(v['bones'][j]), w
        dom.append(by_idx.get(best, ''))
    dom = np.array(dom)
    grp = np.array([(-1 if seg_group(n) is None else seg_group(n)) for n in dom])

    idx = {h['name']: h['index'] for h in R[0][1]['hd2'] if h['name']}
    rest = R[0][1]['rest_shipped']
    a = rest[idx['l_shoulder']][:3, 3].astype(float)
    b = rest[idx['l_hand']][:3, 3].astype(float)
    u = (b - a) / np.linalg.norm(b - a)

    # left-arm region: x < -0.15 and near the axis
    t0 = (P0 - a) @ u
    d0 = P0 - a
    perp0 = np.linalg.norm(d0 - np.outer(t0, u), axis=1)
    region = (P0[:, 0] < -0.15) & (perp0 < 0.16)
    print('left-arm region: %d verts (of %d)' % (int(region.sum()), len(P0)))

    e0 = np.stack([np.linalg.norm(P0[tris[:, 0]] - P0[tris[:, 1]], axis=1),
                   np.linalg.norm(P0[tris[:, 1]] - P0[tris[:, 2]], axis=1),
                   np.linalg.norm(P0[tris[:, 2]] - P0[tris[:, 0]], axis=1)], 1)
    inreg = region[tris].all(1)
    # seam edges = edge whose two ends sit in different arm segments
    edges = np.concatenate([tris[:, [0, 1]], tris[:, [1, 2]], tris[:, [2, 0]]])
    seam = (grp[edges[:, 0]] >= 0) & (grp[edges[:, 1]] >= 0) & \
           (grp[edges[:, 0]] != grp[edges[:, 1]])

    print('\n%-30s %7s %7s %7s %7s %7s %7s' %
          ('', 'p99', 'max', '>2x', '>3x', '>5x', 'seam-max'))
    for tag, r in R:
        P = np.asarray(r['out'], float)
        e = np.stack([np.linalg.norm(P[tris[:, 0]] - P[tris[:, 1]], axis=1),
                      np.linalg.norm(P[tris[:, 1]] - P[tris[:, 2]], axis=1),
                      np.linalg.norm(P[tris[:, 2]] - P[tris[:, 0]], axis=1)], 1)
        ratio = (e / np.maximum(e0, 1e-9))[inreg].max(1)
        # per-EDGE ratio, so the seam test (which is defined on edges) can index it
        e_len = np.concatenate([e[:, 0], e[:, 1], e[:, 2]])
        e0_len = np.concatenate([e0[:, 0], e0[:, 1], e0[:, 2]])
        er_edge = e_len / np.maximum(e0_len, 1e-9)
        smax = float(er_edge[seam].max()) if seam.any() else 0.0
        print('%-30s %7.2f %7.2f %7d %7d %7d %7.2f'
              % (tag, float(np.percentile(ratio, 99)), float(ratio.max()),
                 int((ratio > 2).sum()), int((ratio > 3).sum()),
                 int((ratio > 5).sum()), smax))

    # where are the worst stretched edges of the CURRENT build, along the arm axis?
    P = np.asarray(R[2][1]['out'], float)
    e = np.stack([np.linalg.norm(P[tris[:, 0]] - P[tris[:, 1]], axis=1),
                  np.linalg.norm(P[tris[:, 1]] - P[tris[:, 2]], axis=1),
                  np.linalg.norm(P[tris[:, 2]] - P[tris[:, 0]], axis=1)], 1)
    er = e / np.maximum(e0, 1e-9)
    worst = np.maximum(er[:, 0], np.maximum(er[:, 1], er[:, 2]))
    sel = np.where(inreg & (worst > 3.0))[0]
    print('\ncurrent build: %d triangles in the left arm stretched > 3x' % len(sel))
    if len(sel):
        cen = P[tris[sel]].mean(1)
        tt = (cen - a) @ u
        print('  their t along shoulder->hand (elbow 0.286 / hand 0.555):')
        hist, be = np.histogram(tt, bins=[-0.1, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.8])
        for i in range(len(hist)):
            print('    t %+.2f..%+.2f : %d' % (be[i], be[i + 1], hist[i]))
        c = Counter()
        for ti in sel:
            for vi in tris[ti]:
                c['%s(g%d)' % (dom[vi], grp[vi])] += 1
        print('  dominant bones: %s' % c.most_common(8))

    # ---- figure ----
    rig = E.read_hd2_rig(TOC, BH)
    head = {h['name']: (h['world'][3], h['world'][7], h['world'][11])
            for h in rig if h['name']}
    ed = []
    for h in rig:
        p = h['parent']
        if 0 <= p < len(rig) and h['name'] and rig[p]['name']:
            ed.append((head[rig[p]['name']], head[h['name']]))
    ref = [(nm,) + PV.read_obj(p) for p, nm in REF_UNITS]

    rows = [('front (X-Z)', 0, 2, 1, [-0.80, -1.0, 1.05, -0.15, 1.0, 1.55]),
            ('top (X-Y)  +Y=forward', 0, 1, 2, [-0.80, -0.22, -1.0, -0.15, 0.36, 1.0])]
    cols = [('%s' % tag, r) for tag, r in R] + [('REFERENCE', None)]
    pw, ph = PANEL
    W = 12 + len(cols) * (pw + 14)
    Hh = 66 + len(rows) * (ph + 26) + 16
    img = Image.new('RGB', (W, Hh), (26, 28, 34))
    dr = ImageDraw.Draw(img)
    dr.text((12, 8), 'round 45d: LEFT forearm / wrist / hand -- which build tears '
            'it?', fill=(232, 232, 244))
    dr.text((12, 26), 'cyan = the HD2 shoulder chain, yellow dots = its joints '
            '(clavicle, shoulder, elbow, hand)', fill=(150, 160, 180))
    dr.text((12, 44), 'row 1 front: box x[-0.80,-0.15] z[1.05,1.55]   |   row 2 top: '
            'x[-0.80,-0.15] y[-0.22,0.36]', fill=(150, 160, 180))
    for ci, (clabel, r) in enumerate(cols):
        cx = 12 + ci * (pw + 14)
        dr.text((cx + 4, 62), clabel, fill=(205, 214, 228))
        for ri, (tag, ah, av, ad, box) in enumerate(rows):
            cy = 78 + ri * (ph + 26)
            dr.text((cx + 4, cy - 13), tag, fill=(160, 170, 190))
            p = PV.Panel(img, dr, (cx, cy, cx + pw, cy + ph), box, ah, av, ad, False)
            if r is None:
                for _, rp, rt in ref:
                    p.mesh(rp, rt, (1.0, 0.86, 0.96), STEP)
            else:
                p.mesh([tuple(q) for q in np.asarray(r['out'], float)],
                       r['tris'], (0.78, 0.97, 0.84), STEP)
            for x, y in ed:
                p.line(x, y, (92, 97, 112), 1)
            for nm, par in (('l_clavicle', None), ('l_shoulder', 'l_clavicle'),
                            ('l_elbow', 'l_shoulder'), ('l_hand', 'l_elbow')):
                if nm in head and par in head:
                    p.line(head[par], head[nm], (90, 220, 240), 3)
            for nm in ('l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand'):
                if nm in head:
                    p.dot(head[nm], (255, 210, 90), 4)
    img.save(out)
    print('\n[written] %s  %dx%d' % (out, W, Hh))


if __name__ == '__main__':
    main()
