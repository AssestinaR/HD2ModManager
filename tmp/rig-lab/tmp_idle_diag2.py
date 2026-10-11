import sys
sys.path.insert(0, '.')
import numpy as np
import hd2_style as H
import export_obj as E

VPK = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/Lunalice_Zoey.vpk'
r = H.retarget(VPK, 'survivor_teenangst', 'avatar/unit_0x4d1c334d294dfa97.toc', 'tmp_bh.txt',
               '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json', align='yaw', verbose=False)
verts, src = r['verts'], r['src']
P = np.asarray(r['out_idle']); S = np.asarray(r['src1'])
idx = np.argsort(P[:, 2])[:400]
from collections import Counter
c = Counter()
for k in idx:
    v = verts[k]
    if v['boneCount'] == 0:
        c['<none>'] += 1
        continue
    for j in range(3):
        if v['w'][j] > 0:
            c['%s w=%.2f' % (src[v['bones'][j]]['name'], v['w'][j])] += 1
print('influences on the 400 lowest out_idle vertices:')
for n, ct in c.most_common(12):
    print('   %-34s %d' % (n, ct))

# where does source_extra_62 resolve to?
hd2 = E.read_hd2_rig('avatar/unit_0x4d1c334d294dfa97.toc', 'tmp_bh.txt')
by_idx = {h['index']: h for h in hd2}
pairs = H.snap_pairs('legacy')
short = {b['name'][len(E.SRC_PREFIX):]: b for b in src if b['name'].startswith(E.SRC_PREFIX)}
hby = {h['name']: h for h in hd2 if h['name']}
m = {}
for sn, hn in pairs + H.SNAP_TOES:
    if sn in short and hn in hby:
        m[short[sn]['index']] = hby[hn]['index']
for nm in ('source_extra_62', 'source_extra_63', 'source_extra_61', 'source_extra_64'):
    for b in src:
        if b['name'] == nm:
            cur, hops = b['parent'], 1
            while cur >= 0 and cur not in m and hops <= 64:
                cur = src[cur]['parent']
                hops += 1
            print('%s -> %s (via %s, %d hops)'
                  % (nm, by_idx[m[cur]]['name'] if cur in m else None,
                     src[cur]['name'] if cur >= 0 else '-', hops))
