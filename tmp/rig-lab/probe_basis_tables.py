import json
import math
import os

HERE = os.path.dirname(os.path.abspath(__file__))
p = json.load(open(os.path.join(HERE, '../pb-ref/HD2-BatchTool-main/assets/'
                                'body_pose_profile.json'), encoding='utf-8'))
tp = p['tpose_basis_rotations_wxyz']
ini = p['initial_basis_rotations_from_baked_tpose_wxyz']

print('%-26s %-42s %-42s %s' % ('bone', 'tpose', 'initial', 'relation'))
same = inv = other = 0
for n in sorted(set(tp) | set(ini)):
    a = tp.get(n)
    b = ini.get(n)
    if a is None or b is None:
        rel = 'MISSING'
    elif all(abs(x - y) < 1e-9 for x, y in zip(a, b)):
        rel = 'IDENTICAL'
        same += 1
    elif all(abs(x + y) < 1e-9 for x, y in zip(a, b)):
        rel = 'NEGATED'
        same += 1
    elif all(abs(x - y) < 1e-9 for x, y in zip((a[0], -a[1], -a[2], -a[3]), b)):
        rel = 'INVERSE'
        inv += 1
    else:
        rel = 'DIFFERENT'
        other += 1
    print('%-26s %-42s %-42s %s'
          % (n, ' '.join('%+.5f' % t for t in a) if a else '-',
             ' '.join('%+.5f' % t for t in b) if b else '-', rel))
print('\nidentical/negated %d   inverse %d   different %d' % (same, inv, other))
