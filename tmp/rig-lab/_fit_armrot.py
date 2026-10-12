"""Round 45c: solve --arm-rot (shoulder, elbow, wrist) so the fitted segment
directions match the reference's.

Measured on a FROZEN vertex selection (the arm_rot=None run), so the three rows
are the same vertices every time.  Newton with a numerically probed 3x3 Jacobian.

usage: python _fit_armrot.py [iters] [frozen|live]
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import probe_arm_angle as PA

SEGS = ('upper arm', 'forearm', 'hand')
DELTA = 10.0  # deg, finite-difference step


def main():
    iters = int(sys.argv[1]) if len(sys.argv) > 1 else 4
    mode = sys.argv[2] if len(sys.argv) > 2 else 'frozen'

    base = PA.run(None)
    _, tsel, _, _ = PA.arm_cloud(base)
    if mode == 'live':
        tsel = None
    ref = PA.measure(base, quiet=True)['ref']
    tgt = np.array([ref[s][0] for s in SEGS])

    def f(rot):
        r = base if rot is None else PA.run(rot)
        M = PA.measure(r, quiet=True, tsel=tsel)['ours']
        return np.array([M[s][0] for s in SEGS])

    rot = np.zeros(3)
    cur = f(None)
    print('target %s' % (np.array2string(tgt, precision=2),))
    print('%3s %-22s %-24s %-24s' % ('it', 'rot (SH EL WR)', 'X-Z now', 'X-Z err'))
    for it in range(iters + 1):
        err = tgt - cur
        print('%3d %-22s %-24s %-24s' % (
            it, np.array2string(rot, precision=2, suppress_small=True),
            np.array2string(cur, precision=2), np.array2string(err, precision=2)))
        if it == iters or float(np.abs(err).max()) < 0.15:
            break
        J = np.zeros((3, 3))
        for k in range(3):
            p = rot.copy()
            p[k] += DELTA
            J[:, k] = (f(tuple(p)) - cur) / DELTA
        step = np.linalg.solve(J, err)
        step = np.clip(step, -25.0, 25.0)
        rot = rot + step
        cur = f(tuple(rot))
    print('\nBEST --arm-rot %s' % ' '.join('%.2f' % v for v in rot))


if __name__ == '__main__':
    main()
