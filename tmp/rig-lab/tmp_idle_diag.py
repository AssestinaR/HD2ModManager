import sys, os
sys.path.insert(0, '.')
import numpy as np
import hd2_style as H
import export_obj as E

VPK = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/Lunalice_Zoey.vpk'
r = H.retarget(VPK, 'survivor_teenangst', 'avatar/unit_0x4d1c334d294dfa97.toc', 'tmp_bh.txt',
               '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json', align='yaw', verbose=False)
hd2 = E.read_hd2_rig('avatar/unit_0x4d1c334d294dfa97.toc', 'tmp_bh.txt')
rs, D = r['rest'], r['D']
print('%-22s %9s %9s %9s | %8s' % ('bone', 'our z', 'hd2 z', 'dz', 'angle'))
for h in hd2:
    if not h['name']:
        continue
    if h['name'] in ('hips', 'spine1', 'spine2', 'chest', 'neck', 'head', 'head_tip',
                     'l_thigh', 'l_knee', 'l_foot', 'l_ball', 'l_toe',
                     'r_thigh', 'r_knee', 'r_foot', 'l_shoulder', 'l_elbow', 'l_hand'):
        a = D[h['index']] @ rs[h['index']]
        b = np.array(h['world']).reshape(4, 4)
        ang = np.degrees(np.arccos(np.clip((np.trace(a[:3, :3].T @ b[:3, :3]) - 1) / 2, -1, 1)))
        print('%-22s %9.4f %9.4f %+9.4f | %8.2f' % (h['name'], a[2, 3], b[2, 3], b[2, 3] - a[2, 3], ang))

P = np.asarray(r['out_idle']); S = np.asarray(r['src1'])
idx = np.argsort(P[:, 2])[:400]
print('\nlowest verts: src1 z range %.3f..%.3f -> out_idle z range %.3f..%.3f'
      % (S[idx, 2].min(), S[idx, 2].max(), P[idx, 2].min(), P[idx, 2].max()))
print('shoe centroid src1 %s -> out_idle %s'
      % (np.round(S[idx].mean(0), 3), np.round(P[idx].mean(0), 3)))
