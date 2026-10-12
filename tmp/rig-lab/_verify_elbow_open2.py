# -*- coding: utf-8 -*-
"""Verify --arm-elbow-open, take 2.

Fixes on the first attempt:
  * the displacement buckets are now masked to the LEFT side by DOMINANT BONE,
    not by a geometric window -- the right arm projects to t ~ -0.58 on the left
    arm's axis and was polluting the "torso" bucket with 5.6 cm of motion;
  * the turn angle is measured about the SNAPPED elbow (`r['rest']` = rest_sym),
    which is the pivot the pipeline actually uses, not HD2's native elbow;
  * plus a pivot-free Kabsch rigid fit, which needs no pivot at all.
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
OPEN = float(sys.argv[1]) if len(sys.argv) > 1 else 6.5


def run(open_deg):
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                      spine_map='mesh', fallback='region', leg_tilt=5.7,
                      leg_splay=4.0, skeleton='native',
                      mesh_shift=(0.0, -0.100, -0.090),
                      upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                      head_scale=8.0 / 7.0, arm_rot=(10.46, -18.55, 29.63),
                      arm_elbow_open=open_deg, verbose=False)


def unit(v):
    v = np.asarray(v, float)
    return v / max(float(np.linalg.norm(v)), 1e-12)


r0 = run(0.0)
r1 = run(OPEN)

idx = {h['name']: h['index'] for h in r0['hd2']}
res = r0['resolved']
dom = np.array([res.get(v['bones'][int(np.argmax(v['w']))], -1) for v in r0['verts']])
larm = np.array([bool(n) and n.startswith('l_') and H.is_arm_bone(n)
                 for n in (h['name'] for h in r0['hd2'])])
larm_idx = {h['index'] for h in r0['hd2'] if larm[h['index']]}
is_l = np.isin(dom, list(larm_idx))

snap_el = np.array(r0['rest'][idx['l_elbow']][:3, 3], float)     # the real pivot
hd2_el = np.array(r0['rest_shipped'][idx['l_elbow']][:3, 3], float)
print('snapped elbow %s   HD2 native elbow %s   (%.1f cm apart)'
      % (np.array2string(snap_el, precision=4), np.array2string(hd2_el, precision=4),
         100 * np.linalg.norm(snap_el - hd2_el)))

A = np.asarray(r0['out'], float)
B = np.asarray(r1['out'], float)
d = np.linalg.norm(B - A, axis=1)

sh = np.array(r0['rest_shipped'][idx['l_shoulder']][:3, 3], float)
ha = np.array(r0['rest_shipped'][idx['l_hand']][:3, 3], float)
u = unit(ha - sh)
t = (A - sh) @ u
t_el = float((np.array(r0['rest_shipped'][idx['l_elbow']][:3, 3], float) - sh) @ u)
t_wr = float(np.linalg.norm(ha - sh))

print()
print('LEFT-side-only mesh displacement (m):')
for lo, hi, nm in ((-9.0, 0.05, 'body / above shoulder'),
                   (0.05, t_el, 'upper arm'),
                   (t_el, t_wr, 'forearm'),
                   (t_wr, 9.0, 'hand + fingers')):
    m = is_l & (t >= lo) & (t < hi)
    if m.sum() == 0:
        continue
    print('  %-22s  n=%6d  max %8.5f  mean %8.5f'
          % (nm, int(m.sum()), d[m].max(), d[m].mean()))
m_other = ~is_l
print('  %-22s  n=%6d  max %8.5f  mean %8.5f   <- EVERYTHING ELSE'
      % ('right arm + legs + torso', int(m_other.sum()), d[m_other].max(),
         d[m_other].mean()))

# ---- the turn, about the SNAPPED elbow --------------------------------
sel = is_l & (t >= t_el + 0.02) & (t <= t_wr)
va, vb = A[sel] - snap_el, B[sel] - snap_el
n = np.cross(va, vb)
nn = np.linalg.norm(n, axis=1)
g = nn > 1e-5
ang = np.degrees(np.arctan2(nn[g], (va * vb).sum(1)[g]))
ax = n[g] / nn[g, None]
print()
print('turn about the SNAPPED elbow, n=%d forearm vertices:' % int(sel.sum()))
print('  angle  median %.4f  p05 %.4f  p95 %.4f  deg'
      % (np.median(ang), np.percentile(ang, 5), np.percentile(ang, 95)))
print('  axis   median %s' % np.array2string(np.median(ax, axis=0), precision=4))

# ---- pivot-free rigid check -------------------------------------------
k = min(len(va), 3000)
s = np.linspace(0, len(va) - 1, k).astype(int)
R, ca, cb = H.kabsch(A[sel][s], B[sel][s])
a_k, x_k = H.rot_angle_axis(R)
resid = np.linalg.norm((A[sel][s] - ca) @ R.T + cb - B[sel][s], axis=1)
print()
print('Kabsch rigid fit of the forearm shell, 0 -> %+.1f:' % OPEN)
print('  rotation %.4f deg about %s' % (a_k, np.array2string(x_k, precision=4)))
print('  residual  mean %.3e  max %.3e m  (0 = perfectly rigid)'
      % (resid.mean(), resid.max()))
print('  source-frame plane normal (d_up_tgt x d_fo_tgt) was '
      '(+0.499, +0.006, -0.867) -> negated gives this axis')
