"""How much does the shipped MESH change when the round trip is turned on?"""
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


def run(rt, tilt, splay):
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                      spine_map='mesh', fallback='distance', leg_tilt=tilt,
                      leg_splay=splay, axis_roundtrip=rt, verbose=False)


base = run(False, 0.0, 0.0)          # the original baseline config
for tag, rt, tilt, splay in (('legacy, no stance', False, 0.0, 0.0),
                             ('roundtrip, no stance', True, 0.0, 0.0),
                             ('legacy, tilt 5.7 splay 4', False, 5.7, 4.0),
                             ('roundtrip, tilt 5.7 splay 4', True, 5.7, 4.0)):
    r = run(rt, tilt, splay)
    d = np.linalg.norm(np.asarray(r['out']) - np.asarray(base['out']), axis=1)
    m = r['metrics']
    print('%-26s out vs baseline: mean %.5f  max %.4f m   edge_mean %.4f  bad %.2f%%'
          % (tag, d.mean(), d.max(), m['edge_mean'], 100 * m['bad']))
print()
print('baseline (legacy / no stance): edge_mean %.4f  bad %.2f%%  height %.3f'
      % (base['metrics']['edge_mean'], 100 * base['metrics']['bad'],
         base['metrics']['height_after']))
