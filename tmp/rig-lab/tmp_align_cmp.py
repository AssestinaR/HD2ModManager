import os, sys, math
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
import hd2_style as H

TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
BH = 'tmp_bh.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
VPK = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/Lunalice_Zoey.vpk'
MODEL = 'survivor_teenangst'

hd2 = {h['name']: h for h in E.read_hd2_rig(TOC, BH) if h['name']}
def o(m): return (m[3], m[7], m[11])

print('--- HD2 native joints (reference) ---')
for n in ('hips','spine1','spine2','chest','neck','head','l_thigh','l_knee','l_foot'):
    p = o(hd2[n]['world'])
    print('  %-9s y %+.4f z %.4f' % (n, p[1], p[2]))

KEY = ('l_thigh','neck','head','l_foot','l_knee','spine2','chest')
for align in ('kabsch','yaw','rotz180'):
    r = H.retarget(VPK, MODEL, TOC, BH, PP, align=align, verbose=False)
    nh = r['new_head']
    hip = nh['l_thigh']
    print('\n--- align=%s  scale %.6f ---' % (align, r['scale']))
    print('   %-9s %8s %8s   %s' % ('bone','y','z','delta vs the hip'))
    for n in KEY:
        if n not in nh: continue
        p = nh[n]
        print('   %-9s %+8.4f %8.4f   y %+.4f  z %+.4f' % (n, p[1], p[2], p[1]-hip[1], p[2]-hip[2]))
    # how far the sole is from the HD2 sole in Y
    print('   hip vs HD2 hips:  y %+.4f  z %+.4f' % (hip[1]-o(hd2['hips']['world'])[1],
                                                     hip[2]-o(hd2['hips']['world'])[2]))
