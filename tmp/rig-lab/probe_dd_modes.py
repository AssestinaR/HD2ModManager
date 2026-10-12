"""probe_dd_modes.py -- is `symmetric_axis_deltas` the thing that flips the arm?

`rest_t` (HD2's T-pose working rest) multiplies the SYMMETRIC-axis delta into
each bone's LOCAL matrix, so the delta also moves the bone HEADS.  But the
BatchTool's `convert_body_axis_space` PRESERVES every head -- it only
re-parameterises each bone's own frame.  The delta is 180 deg about +Z on
`r_clavicle`, so multiplying it into the head chain swings the whole right arm
to the other side of the body.

Test: run all four combinations of skeleton x axis_deltas and look at the
resulting mesh bbox.  A healthy A-pose should be left/right symmetric in x with
no vertex far in +Y.
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
kw = dict(align='yaw', spine_map='mesh', fallback='region',
          leg_tilt=5.7, leg_splay=4.0, verbose=False)

print('%-8s %-9s %-26s %-26s %s'
      % ('skeleton', 'deltas', 'out bbox x', 'out bbox y', '|out-src1| max'))
for mode in ('native', 'snap'):
    for ad in (True, False):
        r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                       skeleton=mode, axis_deltas=ad, **kw)
        out = np.asarray(r['out'])
        src1 = np.asarray(r['src1'])
        d = float(np.linalg.norm(out - src1, axis=1).max())
        byn = {h['name']: h['index'] for h in r['hd2'] if h['name']}
        rs = r['rest_shipped'][byn['r_shoulder']][:3, 3]
        print('%-8s %-9s [%+.3f,%+.3f]%9s [%+.3f,%+.3f]%9s %.4f   r_shoulder %s'
              % (mode, 'ON' if ad else 'OFF',
                 out[:, 0].min(), out[:, 0].max(), '',
                 out[:, 1].min(), out[:, 1].max(), '', d,
                 np.round(rs, 3)))
