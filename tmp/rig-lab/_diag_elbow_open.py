# -*- coding: utf-8 -*-
"""Round 52: did the 6.5 deg elbow opening actually move the forearm where the
user wants it?

Two panels, LEFT arm only:
  TOP  view (X-Y, looking down +Z)   -- the view the user read the 6.5 deg off
  FRONT view (X-Z)

grey  = our mesh, elbow as shipped        (arm_elbow_open = 0)
blue  = our mesh, elbow opened +6.5 deg
red   = HD2 native bones  shoulder -> elbow -> hand
cyan  = the REFERENCE patch's own left-arm unit

The thick straight lines are the least-squares axis of the forearm shell
(bin-centroid fit), which is the thing that has to end up parallel to the
reference's forearm.

usage: python _diag_elbow_open.py [deg]
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
DEG = float(sys.argv[1]) if len(sys.argv) > 1 else 6.5


def run(od):
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                      spine_map='mesh', fallback='region', leg_tilt=5.7,
                      leg_splay=4.0, skeleton='native',
                      mesh_shift=(0.0, -0.100, -0.090),
                      upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                      head_scale=8.0 / 7.0, arm_rot=(10.46, -18.55, 29.63),
                      arm_elbow_open=od, verbose=False)


def unit(v):
    v = np.asarray(v, float)
    return v / max(float(np.linalg.norm(v)), 1e-12)


r0, r1 = run(0.0), run(DEG)
idx = {h['name']: h['index'] for h in r0['hd2']}
res = r0['resolved']
dom = np.array([res.get(v['bones'][int(np.argmax(v['w']))], -1) for v in r0['verts']])
larm_idx = {h['index'] for h in r0['hd2']
            if h['name'] and h['name'].startswith('l_') and H.is_arm_bone(h['name'])}
is_l = np.isin(dom, list(larm_idx))

rest = r0['rest_shipped']
sh = np.array(rest[idx['l_shoulder']][:3, 3], float)
el = np.array(rest[idx['l_elbow']][:3, 3], float)
ha = np.array(rest[idx['l_hand']][:3, 3], float)
u = unit(ha - sh)
t_el = float((el - sh) @ u)
t_wr = float(np.linalg.norm(ha - sh))


def axis_of(P):
    t = (P - sh) @ u
    sel = is_l & (t >= t_el + 0.02) & (t <= t_wr)
    if sel.sum() < 40:
        return None, None
    tt, pts = t[sel], P[sel]
    e = np.linspace(t_el + 0.02, t_wr, 9)
    cs = [pts[(tt >= e[i]) & (tt < e[i + 1])].mean(0) for i in range(8)
          if ((tt >= e[i]) & (tt < e[i + 1])).sum() >= 8]
    cs = np.asarray(cs)
    return cs[0], unit(cs[-1] - cs[0])


P0 = np.asarray(r0['out'], float)
P1 = np.asarray(r1['out'], float)
c0, d0 = axis_of(P0)
c1, d1 = axis_of(P1)

# reference left arm
ref = None
for fn in ('out/patchdump/unit-91eb7be84016ceab.obj',
           'out/patchdump/unit-7336c6b662522f0e.obj'):
    try:
        p, _ = PV.read_obj(fn)
        p = np.asarray(p, float)
    except Exception:
        continue
    if p[:, 0].mean() < 0:
        ref = p
        print('reference left arm: %s  n=%d  x[%.3f,%.3f]'
              % (fn, len(p), p[:, 0].min(), p[:, 0].max()))
        break
ref_c = ref_d = None
if ref is not None:
    # ref units are LOD3 shells; fit their forearm the same way
    tt = (ref - sh) @ u
    sel = (tt >= t_el) & (tt <= t_wr)
    if sel.sum() > 30:
        e = np.linspace(t_el, t_wr, 9)
        cs = [ref[sel][(tt[sel] >= e[i]) & (tt[sel] < e[i + 1])].mean(0)
              for i in range(8) if ((tt[sel] >= e[i]) & (tt[sel] < e[i + 1])).sum() >= 4]
        if len(cs) >= 3:
            cs = np.asarray(cs)
            ref_c, ref_d = cs[0], unit(cs[-1] - cs[0])

print('our forearm axis 0 deg   %s' % np.array2string(d0, precision=4))
print('our forearm axis %+.1f  %s' % (DEG, np.array2string(d1, precision=4)))
print('angle between them       %.3f deg'
      % np.degrees(np.arccos(np.clip(abs(d0 @ d1), -1, 1))))
if ref_d is not None:
    print('reference forearm axis   %s' % np.array2string(ref_d, precision=4))
    print('  0 deg vs ref  %.3f deg' % np.degrees(np.arccos(np.clip(abs(d0 @ ref_d), -1, 1))))
    print('  %+.1f vs ref  %.3f deg' % (DEG, np.degrees(np.arccos(np.clip(abs(d1 @ ref_d), -1, 1)))))

# ------------------------------------------------------------------ picture
W, HT = 1360, 760
img = Image.new('RGB', (W, HT), (18, 20, 26))
dr = ImageDraw.Draw(img)


def mkf(box, ux, uy, lo0, hi0, lo1, hi1):
    px0, py0, px1, py1 = box
    sx = (px1 - px0) / float(hi0 - lo0)
    sy = (py1 - py0) / float(hi1 - lo1)
    return lambda a, b: (px0 + (a - lo0) * sx, py1 - (b - lo1) * sy)


TOP = mkf((80, 110, 640, 700), 0, 1, -0.85, 0.05, -0.30, 0.62)
FRT = mkf((760, 110, 1300, 700), 0, 1, -0.85, 0.05, 0.95, 1.62)


def grid(f, box, xs, ys, lx, ly):
    px0, py0, px1, py1 = box
    for a in xs:
        px, _ = f(a, 0)
        dr.line([(px, py0), (px, py1)], fill=(30, 34, 42))
        dr.text((px - 14, py1 + 6), '%+.2f' % a, fill=(95, 105, 122))
    for b in ys:
        _, py = f(0, b)
        dr.line([(px0, py), (px1, py)], fill=(38, 42, 52))
        dr.text((px0 - 46, py - 7), '%+.2f' % b, fill=(95, 105, 122))
    dr.text((px0, py0 - 26), lx, fill=(200, 205, 220))
    dr.text((px0, py0 - 12), ly, fill=(120, 130, 150))


grid(TOP, (80, 110, 640, 700), np.arange(-0.85, 0.06, 0.10),
     np.arange(-0.30, 0.63, 0.10), 'TOP view  (X right, Y forward)',
     'the view the 6.5 deg was read off')
grid(FRT, (760, 110, 1300, 700), np.arange(-0.85, 0.06, 0.10),
     np.arange(0.95, 1.63, 0.10), 'FRONT view  (X, Z)', 'sanity only')

STEP = 26


def cloud(f, P, sel, col):
    for p in P[sel][::STEP]:
        x, y = f(p[0], p[1] if f is TOP else p[2])
        dr.point((x, y), fill=col)


cloud(TOP, P0, is_l, (96, 100, 112))
cloud(FRT, P0, is_l, (96, 100, 112))
cloud(TOP, P1, is_l, (72, 96, 140))
cloud(FRT, P1, is_l, (72, 96, 140))

if ref is not None:
    for p in ref[::STEP]:
        x, y = TOP(p[0], p[1])
        dr.point((x, y), fill=(60, 150, 175))
        x, y = FRT(p[0], p[2])
        dr.point((x, y), fill=(60, 150, 175))


def seg(f, a, b, col, w=3):
    dr.line([f(a[0], a[1]), f(b[0], b[1])], fill=col, width=w)


# HD2 bones
for f in (TOP, FRT):
    seg(f, sh, el, (255, 92, 92))
    seg(f, el, ha, (255, 150, 92))
    for j, nm in ((sh, 'sh'), (el, 'el'), (ha, 'hand')):
        x, y = f(j[0], j[1])
        dr.ellipse([x - 4, y - 4, x + 4, y + 4], fill=(255, 210, 90))
        dr.text((x + 7, y - 6), nm, fill=(255, 210, 90))

# fitted forearm axes, drawn long so the angle is visible
L = 0.30
for f, c, d, col in ((TOP, c0, d0, (215, 218, 228)),
                     (TOP, c1, d1, (110, 165, 255)),
                     (FRT, c0, d0, (215, 218, 228)),
                     (FRT, c1, d1, (110, 165, 255))):
    if c is None:
        continue
    a = c - d * 0.03
    b = c + d * L
    if f is FRT:
        a = c - d * 0.03
        b = c + d * L
        dr.line([f(a[0], a[2]), f(b[0], b[2])], fill=col, width=3)
    else:
        dr.line([f(a[0], a[1]), f(b[0], b[1])], fill=col, width=3)

if ref_d is not None:
    a = ref_c - ref_d * 0.03
    b = ref_c + ref_d * L
    dr.line([TOP(a[0], a[1]), TOP(b[0], b[1])], fill=(80, 220, 250), width=3)
    dr.line([FRT(a[0], a[2]), FRT(b[0], b[2])], fill=(80, 220, 250), width=3)

dr.text((80, 18), 'ELBOW: forearm axis, LEFT arm.  grey cloud = our mesh, '
        'elbow as shipped;  blue cloud = elbow opened +%.1f deg' % DEG,
        fill=(232, 232, 244))
dr.text((80, 36), 'white line = our forearm axis @ 0 deg;  blue line = @ %+.1f deg;  '
        'cyan = REFERENCE patch forearm;  red/orange = HD2 native bones'
        % DEG, fill=(150, 160, 180))
dr.text((80, 54), 'yellow dots = shoulder / elbow / hand joints (HD2 native, cannot move).'
        '   grey dotted verticals mark the elbow plane.', fill=(110, 120, 138))
dr.text((80, 720), 'axis 0 deg: %s    axis %+.1f: %s    turn %.3f deg'
        % (np.array2string(d0, precision=3), DEG,
           np.array2string(d1, precision=3),
           np.degrees(np.arccos(np.clip(abs(d0 @ d1), -1, 1)))),
        fill=(200, 205, 220))

out = os.path.abspath('out/elbow-open-%g.png' % DEG)
img.save(out)
print('\n[written] %s' % out)
