"""Quantify how far the reference patch's mesh deviates from its OWN skeleton.

The patch's bones are bit-identical to HD2's native rig (verified earlier), so any
gap between a joint and the mesh it drives is the *author's* choice, not ours.
"""
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import patch_view as PV

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
DUMP = 'out/patchdump'
UNITS = ['af431352d9b95e83', 'cae35ee15168145f', '17da843a4908511e',
         '91eb7be84016ceab', '7336c6b662522f0e']


def main():
    meta = json.load(open(os.path.join(DUMP, 'units.json'), encoding='utf-8'))
    names = PV.read_bone_names(BH)

    pts, per = [], {}
    base = 0
    for u in meta:
        fid = u['unitFileId'][2:].lower()
        if fid not in UNITS:
            continue
        p, t = PV.read_obj(os.path.join(DUMP, 'unit-%s.obj' % fid))
        if not t:
            continue
        A = np.asarray(p)
        per[fid] = A
        pts.append(A)
    P = np.concatenate(pts)
    print('patch mesh: %d verts, bbox x[%+.3f,%+.3f] y[%+.3f,%+.3f] z[%+.3f,%+.3f]'
          % (len(P), P[:, 0].min(), P[:, 0].max(), P[:, 1].min(), P[:, 1].max(),
             P[:, 2].min(), P[:, 2].max()))

    head_unit = per['af431352d9b95e83']
    print('head/hair unit alone: x[%+.3f,%+.3f] y[%+.3f,%+.3f] z[%.3f,%.3f]  '
          '(height %.3f m)' % (head_unit[:, 0].min(), head_unit[:, 0].max(),
                               head_unit[:, 1].min(), head_unit[:, 1].max(),
                               head_unit[:, 2].min(), head_unit[:, 2].max(),
                               head_unit[:, 2].max() - head_unit[:, 2].min()))
    # the skull only: everything in the head unit above the chin line
    skull = head_unit[head_unit[:, 2] > 1.62]
    print('  skull (z>1.62): x[%+.3f,%+.3f] y[%+.3f,%+.3f] z[%.3f,%.3f]  '
          '-> %.3f m wide, %.3f m deep, %.3f m tall'
          % (skull[:, 0].min(), skull[:, 0].max(), skull[:, 1].min(), skull[:, 1].max(),
             skull[:, 2].min(), skull[:, 2].max(),
             skull[:, 0].max() - skull[:, 0].min(), skull[:, 1].max() - skull[:, 1].min(),
             skull[:, 2].max() - skull[:, 2].min()))

    bones = PV.patch_rig_heads(meta, names)
    hd2 = PV.hd2_rig_heads(TOC, BH)
    print('\n%-16s %-28s %-28s %s' % ('bone', 'patch head (x,y,z)', 'HD2 native (x,y,z)', 'delta'))
    for k in ('hips', 'spine1', 'spine2', 'chest', 'neck', 'head',
              'l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand',
              'l_thigh', 'l_knee', 'l_foot'):
        a, b = bones.get(k), hd2.get(k)
        if not a or not b:
            continue
        dd = max(abs(a[i] - b[i]) for i in range(3))
        print('%-16s (%+.4f,%+.4f,%+.4f)   (%+.4f,%+.4f,%+.4f)   %.6f'
              % (k, a[0], a[1], a[2], b[0], b[1], b[2], dd))

    # how far does the mesh reach past its driving joint?
    def reach(joint, axis, sign, label, mesh):
        j = bones.get(joint)
        if not j:
            return
        v = mesh[:, axis] * sign
        print('  %-14s joint %s=%.4f   mesh reaches %.4f  (mesh is %+.4f m '
              'further out)' % (label, 'xyz'[axis], j[axis] * sign, v.max(),
                                v.max() - j[axis] * sign))

    print('\nhead bone vs head geometry:')
    print('  HD2 native head joint z = %.4f' % hd2['head'][2])
    print('  patch     head joint z = %.4f' % bones['head'][2])
    print('  patch head+hair mesh top z = %.4f  -> geometry rises %.4f m above the '
          'head joint' % (head_unit[:, 2].max(), head_unit[:, 2].max() - bones['head'][2]))
    print('  (HD2 native avatar mesh only rises ~0.024 m above its head joint)')

    print('\narm bone vs sleeve geometry:')
    reach('l_hand', 0, -1, 'left arm', P)
    reach('r_hand', 0, +1, 'right arm', P)
    print('\nfoot bone vs shoe geometry (mesh restricted to z < 0.20):')
    low = P[P[:, 2] < 0.20]
    reach('l_foot', 1, +1, 'left foot', low)
    print('  shoe sole z = %.4f  (HD2 ground is z = 0)' % low[:, 2].min())
    print('\nhip / skirt:')
    sk = P[(P[:, 2] > 0.75) & (P[:, 2] < 1.05)]
    print('  skirt band z[0.75,1.05]: x[%+.3f,%+.3f] y[%+.3f,%+.3f]  -> %.3f m wide, '
          '%.3f m deep' % (sk[:, 0].min(), sk[:, 0].max(), sk[:, 1].min(), sk[:, 1].max(),
                           sk[:, 0].max() - sk[:, 0].min(), sk[:, 1].max() - sk[:, 1].min()))


if __name__ == '__main__':
    main()
