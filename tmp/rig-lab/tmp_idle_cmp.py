import sys
sys.path.insert(0, '.')
import numpy as np
import hd2_style as H

VPK = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/Lunalice_Zoey.vpk'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
BH = 'tmp_bh.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'

print('%-6s %-9s %-9s | %-38s | %s' % ('align', 'spine', 'fallback', 'out_idle bbox (what the game shows)', 'height'))
for align in ('kabsch', 'yaw'):
    for spine in ('legacy', 'mesh'):
        for fb in ('parent', 'distance'):
            r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align=align,
                           spine_map=spine, fallback=fb, verbose=False)
            P = np.asarray(r['out_idle'])
            print('%-6s %-9s %-9s | x[%+.3f,%+.3f] y[%+.3f,%+.3f] z[%+.3f,%+.3f] | %.3f m'
                  % (align, spine, fb, P[:, 0].min(), P[:, 0].max(), P[:, 1].min(),
                     P[:, 1].max(), P[:, 2].min(), P[:, 2].max(), P[:, 2].max() - P[:, 2].min()))
