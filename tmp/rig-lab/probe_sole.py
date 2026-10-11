"""Where does our aligned source actually put the sole?

The pipeline's docstring says it "locks the visible foot sole".  Read the code and
it locks the HEAD joint instead: the sole only lands on HD2's sole in Z, and only
because the scale is derived from the sole->head height.  In Y nothing constrains
it.  This probe measures the resulting offset against the reference patch, whose
mesh sits on HD2's own skeleton and is therefore the ground truth for "where the
feet belong".
"""
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
import hd2_style as H
import patch_view as PV

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
MODEL = 'survivor_teenangst'
REF = ['af431352d9b95e83', 'cae35ee15168145f', '17da843a4908511e',
       '91eb7be84016ceab', '7336c6b662522f0e']


def sole(P, band=0.01, side=-1):
    """Centroid + span of the sole patch of one foot (side=-1 -> x < 0 = HD2 left)."""
    P = np.asarray(P)
    lo = P[:, 2].min()
    m = P[(P[:, 2] < lo + band) & (P[:, 0] * side > 0.05)]
    if len(m) < 5:
        m = P[P[:, 2] < lo + band]
    return (m.mean(0), m[:, 1].min(), m[:, 1].max(), m[:, 0].mean(), lo)


def main():
    r = H.retarget(VPK, MODEL, TOC, BH, PP, align='yaw', spine_map='mesh',
                   fallback='distance', verbose=False)
    src1 = np.asarray(r['src1'])
    out = np.asarray(r['out'])

    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    rp, rt, base = [], [], 0
    for u in meta:
        fid = u['unitFileId'][2:].lower()
        if fid not in REF:
            continue
        p, t = PV.read_obj('out/patchdump/unit-%s.obj' % fid)
        if t:
            rp.extend(p)
            rt.extend([(x[0] + base, x[1] + base, x[2] + base, 0) for x in t])
            base += len(p)
    rp = np.asarray(rp)

    hd2 = E.read_hd2_rig(TOC, BH)
    hb = {h['name']: h for h in hd2 if h['name']}
    for nm in ('l_foot', 'l_thigh', 'head'):
        w = hb[nm]['world']
        print('HD2 native %-8s head = (%+.4f, %+.4f, %+.4f)' % (nm, w[3], w[7], w[11]))

    print('\n%-26s %-24s %-24s %s' % ('', 'sole centroid (x,y,z)', 'foot y span', 'foot x centre'))
    for tag, P in (('OURS (aligned source)', src1), ('OURS (skinned out)', out),
                   ('REFERENCE patch mesh', rp)):
        c, ylo, yhi, xc, lo = sole(P)
        print('%-26s (%+.4f,%+.4f,%+.4f)   [%+.4f,%+.4f]   %+.4f   (sole z %+.4f)'
              % (tag, c[0], c[1], c[2], ylo, yhi, xc, lo))

    c_ours = sole(src1)[0]
    c_ref = sole(rp)[0]
    print('\nOURS - REFERENCE (sole centroid): dx %+.4f  dy %+.4f  dz %+.4f'
          % (c_ours[0] - c_ref[0], c_ours[1] - c_ref[1], c_ours[2] - c_ref[2]))
    print('=> in-game the character would stand %+.1f cm %s and %+.1f cm %s'
          % (abs(c_ours[1] - c_ref[1]) * 100,
             'forward' if c_ours[1] > c_ref[1] else 'backward',
             abs(c_ours[0] - c_ref[0]) * 100,
             'left' if c_ours[0] > c_ref[0] else 'right'))


if __name__ == '__main__':
    main()
