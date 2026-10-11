import json
import math
import os

HERE = os.path.dirname(os.path.abspath(__file__))
p = json.load(open(os.path.join(HERE, '../pb-ref/HD2-BatchTool-main/assets/'
                                'body_pose_profile.json'), encoding='utf-8'))
print('keys:')
for k, v in p.items():
    if isinstance(v, (dict, list)):
        print('  %-52s %-6s %d' % (k, type(v).__name__, len(v)))
    else:
        print('  %-52s %s' % (k, repr(v)[:90]))

print()
d = p['symmetric_axis_deltas_wxyz']
print('symmetric_axis_deltas_wxyz (%d):' % len(d))
for k, q in d.items():
    w, x, y, z = q
    ang = math.degrees(2 * math.acos(max(-1.0, min(1.0, abs(w)))))
    s = math.sqrt(max(0.0, 1 - w * w))
    ax = (x / s, y / s, z / s) if s > 1e-9 else (0, 0, 0)
    print('  %-24s %7.1f deg  axis (%+.3f %+.3f %+.3f)  raw %s'
          % (k, ang, ax[0], ax[1], ax[2], ['%+.4f' % t for t in q]))
