"""Round 45d: is the forearm S-wave caused by the weight-field smoothing?

Same tight camera on the LEFT forearm for: no arm_rot / seq with 0, 2, 8
smoothing passes / blend with 8 / reference.

usage: python _ab_armsmooth.py [out.png]
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
REF = [('out/patchdump/unit-91eb7be84016ceab.obj', 'LeftArm_Undergarment_Slim')]
AR = (10.46, -18.55, 29.63)
BASE = dict(align='yaw', spine_map='mesh', fallback='region', leg_tilt=5.7,
            leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060), upper_scale=1.12,
            upper_pivot_z=1.15, upper_blend=0.45, arm_fit=1.0, verbose=False)


def run(**kw):
    b = dict(BASE); b.update(kw)
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, **b)


def main():
    out = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else
                          'out/arm-smooth.png')
    builds = [('no arm_rot', dict(arm_rot=None)),
              ('seq  smooth 0', dict(arm_rot=AR, arm_rot_smooth=0)),
              ('seq  smooth 2', dict(arm_rot=AR, arm_rot_smooth=2)),
              ('seq  smooth 8', dict(arm_rot=AR, arm_rot_smooth=8)),
              ('blend smooth 8', dict(arm_rot=AR, arm_rot_smooth=8,
                                      arm_rot_mode='blend')),
              ('REFERENCE', None)]
    R = [(t, None if k is None else run(**k)) for t, k in builds]
    P0 = np.asarray(R[0][1]['out'], float)
    tris = np.asarray([t[:3] for t in R[0][1]['tris']])
    e0 = np.stack([np.linalg.norm(P0[tris[:, 0]] - P0[tris[:, 1]], axis=1),
                   np.linalg.norm(P0[tris[:, 1]] - P0[tris[:, 2]], axis=1),
                   np.linalg.norm(P0[tris[:, 2]] - P0[tris[:, 0]], axis=1)], 1)
    idx = {h['name']: h['index'] for h in R[0][1]['hd2'] if h['name']}
    rest = R[0][1]['rest_shipped']
    a = rest[idx['l_shoulder']][:3, 3].astype(float)
    b = rest[idx['l_hand']][:3, 3].astype(float)
    u = (b - a) / np.linalg.norm(b - a)
    t0 = (P0 - a) @ u
    perp0 = np.linalg.norm((P0 - a) - np.outer(t0, u), axis=1)
    region = (P0[:, 0] < -0.15) & (perp0 < 0.16)
    inreg = region[tris].all(1)
    print('%-16s %7s %7s %7s' % ('', 'p99', 'max', '>2x'))
    for tag, r in R:
        if r is None:
            continue
        P = np.asarray(r['out'], float)
        e = np.stack([np.linalg.norm(P[tris[:, 0]] - P[tris[:, 1]], axis=1),
                      np.linalg.norm(P[tris[:, 1]] - P[tris[:, 2]], axis=1),
                      np.linalg.norm(P[tris[:, 2]] - P[tris[:, 0]], axis=1)], 1)
        rt = (e / np.maximum(e0, 1e-9)).max(1)[inreg]
        print('%-16s %7.2f %7.2f %7d'
              % (tag, float(np.percentile(rt, 99)), float(rt.max()),
                 int((rt > 2).sum())))

    rig = E.read_hd2_rig(TOC, BH)
    head = {h['name']: (h['world'][3], h['world'][7], h['world'][11])
            for h in rig if h['name']}
    ed = []
    for h in rig:
        p = h['parent']
        if 0 <= p < len(rig) and h['name'] and rig[p]['name']:
            ed.append((head[rig[p]['name']], head[h['name']]))
    ref = [(nm,) + PV.read_obj(p) for p, nm in REF]
    rows = [('front (X-Z)', 0, 2, 1, [-0.80, -1.0, 1.05, -0.15, 1.0, 1.55]),
            ('top (X-Y)', 0, 1, 2, [-0.80, -0.22, -1.0, -0.15, 0.36, 1.0])]
    pw, ph = 520, 470
    W = 12 + len(R) * (pw + 14)
    Hh = 66 + len(rows) * (ph + 26) + 16
    img = Image.new('RGB', (W, Hh), (26, 28, 34))
    dr = ImageDraw.Draw(img)
    dr.text((12, 8), 'round 45d: does the weight-field smoothing create the '
            'forearm S-wave?', fill=(232, 232, 244))
    dr.text((12, 26), 'cyan = the HD2 shoulder chain, yellow dots = its joints',
            fill=(150, 160, 180))
    for ci, (tag, r) in enumerate(R):
        cx = 12 + ci * (pw + 14)
        dr.text((cx + 4, 62), tag, fill=(205, 214, 228))
        for ri, (rtag, ah, av, ad, box) in enumerate(rows):
            cy = 78 + ri * (ph + 26)
            dr.text((cx + 4, cy - 13), rtag, fill=(160, 170, 190))
            p = PV.Panel(img, dr, (cx, cy, cx + pw, cy + ph), box, ah, av, ad, False)
            if r is None:
                for _, rp, rtt in ref:
                    p.mesh(rp, rtt, (1.0, 0.86, 0.96), 1)
            else:
                p.mesh([tuple(q) for q in np.asarray(r['out'], float)],
                       r['tris'], (0.78, 0.97, 0.84), 1)
            for x, y in ed:
                p.line(x, y, (92, 97, 112), 1)
            for nm, par in (('l_shoulder', 'l_clavicle'), ('l_elbow', 'l_shoulder'),
                            ('l_hand', 'l_elbow')):
                if nm in head and par in head:
                    p.line(head[par], head[nm], (90, 220, 240), 3)
            for nm in ('l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand'):
                if nm in head:
                    p.dot(head[nm], (255, 210, 90), 4)
    img.save(out)
    print('[written] %s %dx%d' % (out, W, Hh))


if __name__ == '__main__':
    main()
