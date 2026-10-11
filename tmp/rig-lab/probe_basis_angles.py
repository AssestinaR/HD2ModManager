import json
import math
import os

HERE = os.path.dirname(os.path.abspath(__file__))
p = json.load(open(os.path.join(HERE, '../pb-ref/HD2-BatchTool-main/assets/'
                                'body_pose_profile.json'), encoding='utf-8'))


def ang(q):
    w = max(-1.0, min(1.0, abs(q[0])))
    return math.degrees(2 * math.acos(w))


tp = p['tpose_basis_rotations_wxyz']
ini = p['initial_basis_rotations_from_baked_tpose_wxyz']
print('tpose_basis_rotations_wxyz      : %d entries' % len(tp))
print('initial_basis_rotations_from_baked_tpose_wxyz : %d entries' % len(ini))
print()
print('%-26s %10s %10s' % ('bone', 'tpose deg', 'initial deg'))
names = sorted(set(tp) | set(ini))
for n in names:
    a = ang(tp[n]) if n in tp else float('nan')
    b = ang(ini[n]) if n in ini else float('nan')
    print('%-26s %10.1f %10.1f' % (n, a, b))

print()
print('transform_policy:', json.dumps(p['transform_policy'], ensure_ascii=False))
print('validation      :', json.dumps(p['validation'], ensure_ascii=False))
print('reference_group :', p['reference_group'])
