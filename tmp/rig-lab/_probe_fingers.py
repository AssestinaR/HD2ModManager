"""Source fingers vs HD2 fingers, bone by bone.

The source rig (ValveBiped) carries a FULL 5-finger x 3-phalanx hand:
    Finger0/01/02 = thumb, Finger1/11/12 = index, Finger2/21/22 = middle,
    Finger3/31/32 = ring,  Finger4/41/42 = pinky
and HD2 carries the same tree under different names:
    l_thumb_finger1/2/3, l_index_..., l_middle_..., l_ring_..., l_pinky_...

So the trees are isomorphic -- this probe measures whether the *directions*
are close enough to copy, or whether the hand needs its own correction the way
the elbow did.

NOTE the poses differ: the source bind pose is a T-pose (arm horizontal), HD2's
native rest is an A-pose (arm hanging ~30 deg).  So we compare directions
*relative to the parent chain* (which is pose-invariant if the arm is rigid).

usage: python _probe_fingers.py
"""
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
import hd2_style as H

VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
MODEL = 'survivor_teenangst'
BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'

# (source name suffix, hd2 name) for the left hand; right is derived by mirror.
FINGERS = [
    ('Finger0', 'thumb'), ('Finger1', 'index'), ('Finger2', 'middle'),
    ('Finger3', 'ring'), ('Finger4', 'pinky'),
]


def ang(u, v):
    u = np.asarray(u, float)
    v = np.asarray(v, float)
    c = float(u @ v) / max(np.linalg.norm(u) * np.linalg.norm(v), 1e-12)
    return math.degrees(math.acos(max(-1.0, min(1.0, c))))


def tri(u):
    u = u / max(np.linalg.norm(u), 1e-12)
    return (math.degrees(math.atan2(-u[2], abs(u[0]))),
            math.degrees(math.atan2(abs(u[1]), abs(u[0]))),
            math.degrees(math.atan2(-u[2], abs(u[1]))))


def main():
    _p, _m, _v, _t, src, _short = H.load_source(VPK, MODEL)
    def sname(b):
        n = b['name'].split('.')[-1]
        return n[len('Bip01_'):] if n.startswith('Bip01_') else n
    sw = {sname(b): b for b in src}
    hd2 = E.read_hd2_rig(TOC, BH)
    hw = {h['name']: np.asarray(h['world'], float).reshape(4, 4)
          for h in hd2 if h['name']}

    def s_pos(n):
        return np.asarray(sw[n]['world_m'], float).reshape(4, 4)[:3, 3]

    def h_pos(n):
        return hw[n][:3, 3]

    print('=== left hand chain, positions (m) ===')
    print('%-26s %-22s %-22s' % ('bone', 'SOURCE (T-pose)', 'HD2 (native)'))
    rows = [('L_Hand', 'l_hand')]
    for sfx, hn in FINGERS:
        rows.append(('L_' + sfx, 'l_' + hn + '_finger1'))
        rows.append(('L_' + sfx + '1', 'l_' + hn + '_finger2'))
        rows.append(('L_' + sfx + '2', 'l_' + hn + '_finger3'))
    for sn, hn in rows:
        sp = s_pos(sn)
        hp = h_pos(hn)
        print('%-26s %+7.4f %+7.4f %+7.4f   %+7.4f %+7.4f %+7.4f'
              % (sn, sp[0], sp[1], sp[2], hp[0], hp[1], hp[2]))

    print()
    print('=== each phalanx: length + direction, SOURCE vs HD2 ===')
    print('%-22s %7s %7s   %-24s %-24s %6s'
          % ('phalanx', 'srcLen', 'hd2Len', 'src front/top/side',
             'hd2 front/top/side', 'angle'))
    chains = []
    for sfx, hn in FINGERS:
        chains.append((hn,
                       [('L_Hand', 'L_' + sfx),
                        ('L_' + sfx, 'L_' + sfx + '1'),
                        ('L_' + sfx + '1', 'L_' + sfx + '2')],
                       [('l_hand', 'l_' + hn + '_finger1'),
                        ('l_' + hn + '_finger1', 'l_' + hn + '_finger2'),
                        ('l_' + hn + '_finger2', 'l_' + hn + '_finger3')]))
    for hn, sc, hc in chains:
        for k, ((sa, sb), (ha, hb)) in enumerate(zip(sc, hc)):
            ds = s_pos(sb) - s_pos(sa)
            dh = h_pos(hb) - h_pos(ha)
            ts = tri(ds)
            th = tri(dh)
            print('%-22s %7.4f %7.4f   %6.1f %6.1f %6.1f    %6.1f %6.1f %6.1f   %6.1f'
                  % ('%s seg%d' % (hn, k + 1), np.linalg.norm(ds),
                     np.linalg.norm(dh), ts[0], ts[1], ts[2],
                     th[0], th[1], th[2], ang(ds, dh)))

    print()
    print('=== palm axis: l_hand -> middle_finger1 ===')
    for lab, a, b in (('source L_Hand->L_Finger2', 'L_Hand', 'L_Finger2'),
                      ('HD2 l_hand->l_middle_finger1', 'l_hand',
                       'l_middle_finger1')):
        d = (s_pos(b) - s_pos(a)) if lab.startswith('source') \
            else (h_pos(b) - h_pos(a))
        t = tri(d)
        print('  %-28s len %.4f  front %6.1f  top %6.1f  side %6.1f'
              % (lab, np.linalg.norm(d), t[0], t[1], t[2]))

    print()
    print('=== finger spread: angle from the middle finger, per finger ===')
    print('%-10s %-16s %-16s' % ('finger', 'SOURCE', 'HD2'))
    for sfx, hn in FINGERS:
        ds = s_pos('L_' + sfx) - s_pos('L_Hand')
        dh = h_pos('l_' + hn + '_finger1') - h_pos('l_hand')
        dm = s_pos('L_Finger2') - s_pos('L_Hand')
        dmh = h_pos('l_middle_finger1') - h_pos('l_hand')
        print('%-10s %6.1f deg       %6.1f deg'
              % (hn, ang(ds, dm), ang(dh, dmh)))


if __name__ == '__main__':
    main()
