"""Do the SOURCE per-finger bones actually drive any vertices?

Before we spend effort retargeting 30 finger bones we must know whether the
L4D2 mesh is even skinned to them.  A rig can carry the bones and still bind
every hand vertex to L_Hand -- in that case nothing we do to the finger bones
can move a single vertex.

Reports, for each source hand bone:
  * how many vertices carry any weight on it (w > 0)
  * the total weight mass, and the max single weight
  * the bounding box of the vertices it dominates (w > 0.5)

usage: python _probe_fingerweights.py
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H

VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
MODEL = 'survivor_teenangst'


def main():
    _p, _m, verts, _t, src, _short = H.load_source(VPK, MODEL)

    def sname(b):
        n = b['name'].split('.')[-1]
        return n[len('Bip01_'):] if n.startswith('Bip01_') else n
    idx_name = {b['index']: sname(b) for b in src}

    nv = len(verts)
    print('vertices %d  source bones %d' % (nv, len(src)))

    # weight accumulation per bone
    nz = np.zeros(len(src), int)          # vertices with w > 0
    mass = np.zeros(len(src))
    wmax = np.zeros(len(src))
    dom_bb = {}
    for k, v in enumerate(verts):
        for j in range(3):
            w = float(v['w'][j])
            if w <= 0.0:
                continue
            b = int(v['bones'][j])
            nz[b] += 1
            mass[b] += w
            if w > wmax[b]:
                wmax[b] = w
            if w > 0.5:
                p = np.asarray(v['pos'], float) * 0.0254  # inches -> metres
                lo, hi = dom_bb.get(b, (p.copy(), p.copy()))
                dom_bb[b] = (np.minimum(lo, p), np.maximum(hi, p))

    print()
    print('--- hand + finger bones (left) ---')
    keys = ['L_Hand'] + ['L_Finger%d' % i for i in range(5)] + \
           ['L_Finger%d%d' % (i, j) for i in range(5) for j in (1, 2)]
    order = ['L_Hand', 'L_Finger0', 'L_Finger01', 'L_Finger02',
             'L_Finger1', 'L_Finger11', 'L_Finger12',
             'L_Finger2', 'L_Finger21', 'L_Finger22',
             'L_Finger3', 'L_Finger31', 'L_Finger32',
             'L_Finger4', 'L_Finger41', 'L_Finger42']
    print('%-14s %7s %10s %8s   %s' % ('bone', 'nverts', 'mass', 'wmax', 'dominated bbox (w>0.5)'))
    for want in order:
        b = next((bb['index'] for bb in src if sname(bb) == want), None)
        if b is None:
            print('%-14s   MISSING' % want)
            continue
        bb = dom_bb.get(b)
        bbs = ('x[%+.3f,%+.3f] y[%+.3f,%+.3f] z[%+.3f,%+.3f]'
               % (bb[0][0], bb[1][0], bb[0][1], bb[1][1],
                  bb[0][2], bb[1][2])) if bb else '-'
        print('%-14s %7d %10.2f %8.3f   %s'
              % (want, nz[b], mass[b], wmax[b], bbs))

    print()
    print('--- top 12 source bones by vertex count (sanity) ---')
    top = np.argsort(-nz)[:12]
    for b in top:
        print('  %-22s nverts %6d  mass %9.1f  wmax %.3f'
              % (idx_name.get(int(b), '?'), nz[b], mass[b], wmax[b]))

    print()
    tot_finger = sum(nz[next(bb['index'] for bb in src if sname(bb) == w)]
                     for w in order[1:])
    print('vertices touched by ANY finger bone: %d / %d  (%.1f%%)'
          % (tot_finger, nv, 100.0 * tot_finger / nv))


if __name__ == '__main__':
    main()
