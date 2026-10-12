"""Round 45e step 3: split the mesh into SHELLS (connected components) and render
each shell in its own colour, so "which shell is the arm and which is the sleeve"
becomes obvious instead of a guess.

A game character is not one surface: bare arm, sleeve, wrist cuff, elbow frill and
gear are usually separate sub-meshes that do not share vertices.  Connected
components recover exactly that, with no material index needed.

usage: python probe_layered_render.py
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
ROT = (10.46, -18.55, 29.63)
PW, PH = 620, 620
BOX = [-0.735, -1.0, 1.135, -0.415, 1.0, 1.400]
PALETTE = [(255, 90, 90), (90, 220, 255), (255, 215, 80), (140, 255, 140),
           (255, 140, 255), (170, 170, 255), (255, 170, 90), (120, 255, 220),
           (220, 220, 220), (200, 120, 60), (100, 160, 255), (255, 110, 160)]


def run(**kw):
    base = dict(align='yaw', spine_map='mesh', fallback='region', leg_tilt=5.7,
                leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.090),
                upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                verbose=False)
    base.update(kw)
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, **base)


def components(nv, tris):
    """union-find over triangle corners -> label per vertex."""
    par = np.arange(nv, dtype=np.int64)

    def find(x):
        while par[x] != x:
            par[x] = par[par[x]]
            x = par[x]
        return x

    for t in tris:
        a, b, c = int(t[0]), int(t[1]), int(t[2])
        ra, rb, rc = find(a), find(b), find(c)
        if rb != ra:
            par[rb] = ra
        if rc != ra:
            par[rc] = ra
    lab = np.array([find(i) for i in range(nv)], dtype=np.int64)
    return lab


def main():
    r = run(arm_fit=1.0, arm_rot=ROT, arm_rot_mode='axis')
    P = np.asarray(r['out'], float)
    verts = r['verts']
    tris = r['tris']
    by_idx = {h['index']: h['name'] for h in r['hd2'] if h['name']}
    res = r['resolved']
    idx = {h['name']: h['index'] for h in r['hd2'] if h['name']}
    rest = r['rest_shipped']
    a = rest[idx['l_shoulder']][:3, 3].astype(float)
    b = rest[idx['l_hand']][:3, 3].astype(float)
    u = (b - a) / np.linalg.norm(b - a)
    t = (P - a) @ u
    perp = np.linalg.norm((P - a) - np.outer(t, u), axis=1)

    lab = components(len(P), tris)
    x0, y0, x1, y1 = -0.76, 1.10, -0.40, 1.42
    inarm = ((P[:, 0] > x0) & (P[:, 0] < x1) & (P[:, 2] > y0) & (P[:, 2] < y1))
    print('verts in arm box: %d' % inarm.sum())

    dom = []
    for v in verts:
        best, bw = None, -1.0
        for q in range(3):
            if v['w'][q] > bw:
                best, bw = v['bones'][q], v['w'][q]
        dom.append(by_idx.get(res.get(best, -1), '?'))

    # which components live in the arm box?
    t3 = np.asarray([z[:3] for z in tris])
    comps = {}
    for k, l in enumerate(np.unique(lab)):
        sel = lab == l
        nv = int(sel.sum())
        if nv < 30:
            continue
        cen = P[sel].mean(0)
        if not (x0 < cen[0] < x1 and y0 < cen[2] < y1):
            continue
        tv = t[t3][np.asarray(sel)[t3].all(1)]
        comps[int(l)] = dict(nv=nv, ntri=int((np.asarray(sel)[t3]).all(1).sum()),
                             cen=cen, perp=float(perp[sel].mean()),
                             tmin=float(t[sel].min()), tmax=float(t[sel].max()))
    order = sorted(comps.items(), key=lambda kv: -kv[1]['nv'])
    print('\n%-4s %8s %8s %8s %8s %8s  %s' %
          ('#', 'verts', 'tris', 'perp', 't_min', 't_max', 'top bones'))
    for i, (k, d) in enumerate(order):
        sel = lab == k
        names = {}
        for j in np.flatnonzero(sel):
            names[dom[j]] = names.get(dom[j], 0) + 1
        top = sorted(names.items(), key=lambda kv: -kv[1])[:3]
        print('%-4d %8d %8d %8.3f %8.3f %8.3f  %s'
              % (i, d['nv'], d['ntri'], d['perp'], d['tmin'], d['tmax'],
                 ', '.join('%s:%d' % kv for kv in top)))
        comps[k]['colour'] = PALETTE[i % len(PALETTE)]

    img = Image.new('RGB', (PW + 24, PH + 150), (0, 0, 0))
    dr = ImageDraw.Draw(img)
    dr.text((12, 8), 'round 45e step 3: the left arm split into SHELLS '
            '(connected components), each a different colour', fill=(232, 232, 244))
    dr.text((12, 26), 'box x[-0.735,-0.415] z[1.135,1.400].  if the sleeve and the '
            'bare arm are separate shells they get separate colours --', fill=(150, 160, 180))
    dr.text((12, 44), 'then the centre line can be taken from ONE shell only, which '
            'is what the plain "black + white" render could not do.', fill=(150, 160, 180))
    p = PV.Panel(img, dr, (12, 62, 12 + PW, 62 + PH), BOX, 0, 2, 1, False)
    for i, (k, d) in enumerate(order):
        sub = [z for z in tris if int(lab[int(z[0])]) == k]
        p.mesh([tuple(q) for q in P], sub, tuple(c / 255.0 for c in d['colour']), 1)
    ly = 62 + PH + 8
    for i, (k, d) in enumerate(order[:8]):
        col = d['colour']
        dr.rectangle([12 + i * 150, ly + 4, 12 + i * 150 + 14, ly + 18], fill=col)
        dr.text((12 + i * 150 + 20, ly + 5),
                '#%d  %dv  perp %.2f' % (i, d['nv'], d['perp']), fill=(210, 218, 232))
    out = os.path.abspath('out/layered-shells.png')
    img.save(out)
    print('\n[written] %s' % out)


if __name__ == '__main__':
    main()
