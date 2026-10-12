"""Print the shipped shoulder-chain joint positions + the arm-mesh extent."""
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

r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
               spine_map='mesh', fallback='region', leg_tilt=5.7, leg_splay=4.0,
               mesh_shift=(0.0, -0.100, -0.060), upper_scale=1.12,
               upper_pivot_z=1.15, upper_blend=0.45, arm_fit=1.0,
               arm_rot=(9.90, -17.25, 21.50), verbose=False)

idx = {h['name']: h['index'] for h in r['hd2'] if h['name']}
rest = r['rest_shipped']
print('== rest_shipped (published rig, = HD2 native) ==')
for nm in ('l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand', 'l_index_finger1',
           'l_index_finger3', 'l_middle_finger3'):
    if nm in idx:
        p = rest[idx[nm]][:3, 3]
        print('  %-22s %+8.4f %+8.4f %+8.4f' % (nm, p[0], p[1], p[2]))

P = np.asarray(r['out'], float)
tris = np.asarray([t[:3] for t in r['tris']])
print('\n== mesh overall ==')
print('  x[%+.3f,%+.3f] y[%+.3f,%+.3f] z[%+.3f,%+.3f]'
      % (P[:, 0].min(), P[:, 0].max(), P[:, 1].min(), P[:, 1].max(),
         P[:, 2].min(), P[:, 2].max()))

# arm cloud along the shipped shoulder->hand axis
a = rest[idx['l_shoulder']][:3, 3].astype(float)
b = rest[idx['l_hand']][:3, 3].astype(float)
u = (b - a) / np.linalg.norm(b - a)
t = (P - a) @ u
print('\n  shoulder->hand axis len %.4f  t range of mesh [%+.3f,%+.3f]'
      % (np.linalg.norm(b - a), t.min(), t.max()))
# vertices "on the arm" = within 12 cm of the axis and t in [-0.1, 0.8]
d = P - a
perp = np.linalg.norm(d - np.outer(t, u), axis=1)
sel = (np.abs(t - 0.5) < 0.5) & (perp < 0.12)
print('  arm-ish verts %d ; bbox x[%+.3f,%+.3f] y[%+.3f,%+.3f] z[%+.3f,%+.3f]'
      % (int(sel.sum()), P[sel, 0].min(), P[sel, 0].max(), P[sel, 1].min(),
         P[sel, 1].max(), P[sel, 2].min(), P[sel, 2].max()))
# hand region
selh = (t > 0.60) & (perp < 0.14)
print('  hand-ish verts %d ; bbox x[%+.3f,%+.3f] y[%+.3f,%+.3f] z[%+.3f,%+.3f]'
      % (int(selh.sum()), P[selh, 0].min(), P[selh, 0].max(), P[selh, 1].min(),
         P[selh, 1].max(), P[selh, 2].min(), P[selh, 2].max()))
# forearm band
self_ = (t > 0.30) & (t < 0.58) & (perp < 0.12)
print('  forearm verts %d ; bbox x[%+.3f,%+.3f] y[%+.3f,%+.3f] z[%+.3f,%+.3f]'
      % (int(self_.sum()), P[self_, 0].min(), P[self_, 0].max(), P[self_, 1].min(),
         P[self_, 1].max(), P[self_, 2].min(), P[self_, 2].max()))
