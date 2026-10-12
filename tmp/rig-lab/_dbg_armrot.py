"""Round 45c debug: why does the hand not follow the net rotation?

Report, for off and for a given --arm-rot, the hand cloud's own geometry relative
to the wrist joint: centroid offset, farthest point, and the fitted direction.
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import probe_arm_angle as PA

CFGS = [(None, 'off'), ((11.0, -18.0, 15.0), '+11 -18 +15'),
        ((0.0, 0.0, 15.0), '  0   0 +15'),
        ((11.0, 0.0, 0.0), '+11   0   0'),
        ((0.0, -18.0, 0.0), '  0 -18   0')]


def main():
    for rot, tag in CFGS:
        r = PA.run(rot)
        idx = {h['name']: h['index'] for h in r['hd2']}
        rest = r['rest_shipped']
        head = lambda nm: np.array(rest[idx[nm]][:3, 3], float)
        Jsh, Jel, Jwr = head('l_shoulder'), head('l_elbow'), head('l_hand')
        u = (Jwr - Jsh) / np.linalg.norm(Jwr - Jsh)
        t_wr = float(np.linalg.norm(Jwr - Jsh))
        P, t, _, _ = PA.arm_cloud(r)
        sel = (t >= t_wr) & (t < t_wr + 0.14)
        Q = P[sel]
        d = PA.fit_dir(P, t, t_wr, t_wr + 0.14)[0]
        rel = Q - Jwr
        # fingertip = the vertex farthest from the wrist along the hand direction
        far = Q[np.argmax(np.linalg.norm(rel, axis=1))] - Jwr
        print('%-12s hand cloud n=%5d  centroid rel wrist (%+.3f,%+.3f,%+.3f) '
              '|far| %.3f rel (%+.3f,%+.3f,%+.3f)  fit X-Z %6.2f  X-Y %+6.2f'
              % (tag, int(sel.sum()), rel.mean(0)[0], rel.mean(0)[1], rel.mean(0)[2],
                 float(np.linalg.norm(far)), far[0], far[1], far[2],
                 PA.ang_xz(d), PA.ang_xy(d)))
        # how much did the cloud actually move?
        print('             t range %.3f..%.3f' % (t[sel].min(), t[sel].max()))


if __name__ == '__main__':
    main()
