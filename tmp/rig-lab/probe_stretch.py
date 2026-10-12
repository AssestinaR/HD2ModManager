"""Which vertices get stretched in `native` mode, and by which bones?"""
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


def main():
    for mode in ('snap', 'native'):
        r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                       spine_map='mesh', fallback='distance', leg_tilt=5.7,
                       leg_splay=4.0, skeleton=mode, verbose=False)
        src1 = np.asarray(r['src1'])
        out = np.asarray(r['out'])
        verts = r['verts']
        idx2name = {h['index']: h['name'] for h in r['hd2']}
        resolved = r['resolved']
        d = np.linalg.norm(out - src1, axis=1)
        order = np.argsort(-d)[:10]
        print('== %s ==  worst vertex displacement (src1 -> out):' % mode)
        for k in order:
            v = verts[k]
            bones = [(idx2name.get(resolved.get(v['bones'][j])), round(float(v['w'][j]), 3))
                     for j in range(3) if v['w'][j] > 0]
            print('  v%-6d |d| %7.3f m   weights %s   src1 (%.3f %.3f %.3f) -> out '
                  '(%.3f %.3f %.3f)'
                  % (k, d[k], bones, *src1[k], *out[k]))
        big = int((d > 0.5).sum())
        print('  vertices moved > 0.5 m: %d of %d' % (big, len(d)))
        # which bones own those vertices
        from collections import Counter
        c = Counter()
        for k in np.where(d > 0.5)[0]:
            v = verts[k]
            j = int(np.argmax(v['w']))
            c[idx2name.get(resolved.get(v['bones'][j]))] += 1
        print('  owners of those vertices: %s' % c.most_common(8))
        print('  by_dist fallback: %s'
              % ', '.join('%s<-%s(%.3f)' % (r2[0], r2[1], r2[2])
                          for r2 in sorted(r['metrics'].get('hops', {}).items())[:0])
              or '  (see retarget say)')
        print()


if __name__ == '__main__':
    main()
