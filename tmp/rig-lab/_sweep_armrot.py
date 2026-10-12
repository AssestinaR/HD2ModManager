"""Round 45c: sweep --arm-rot and report the per-segment direction, with the
vertex selection FROZEN on the arm_rot=None run so the rows are comparable.

usage: python _sweep_armrot.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import probe_arm_angle as PA

CFGS = [
    (None, 'off'),
    ((11.0, -18.0, 15.0), '+11 -18 +15'),
    ((11.0, -18.0, 8.0), '+11 -18  +8'),
    ((11.0, -18.0, 21.0), '+11 -18 +21'),
    ((10.5, -19.5, 15.0), '+10.5 -19.5 +15'),
    ((14.0, -10.0, 15.0), '+14 -10 +15'),
    ((11.0, 0.0, 0.0), '+11   0   0'),
    ((0.0, -18.0, 0.0), '  0 -18   0'),
    ((0.0, 0.0, 15.0), '  0   0 +15'),
]


def main():
    base = PA.run(None)
    _, tsel, _, _ = PA.arm_cloud(base)
    ref = PA.measure(base, quiet=True)['ref']
    print('ref      ' + ' | '.join(
        '%s X-Z %6.2f X-Y %+6.2f' % (k, v[0], v[1]) for k, v in ref.items()))
    print('%-16s %8s %8s %8s   %8s %8s %8s' % (
        'arm-rot', 'up X-Z', 'fore X-Z', 'hand X-Z', 'up X-Y', 'fore X-Y', 'hand X-Y'))
    for rot, tag in CFGS:
        r = base if rot is None else PA.run(rot)
        M = PA.measure(r, quiet=True, tsel=tsel)['ours']
        print('%-16s %8.2f %8.2f %8.2f   %+8.2f %+8.2f %+8.2f' % (
            tag, M['upper arm'][0], M['forearm'][0], M['hand'][0],
            M['upper arm'][1], M['forearm'][1], M['hand'][1]))


if __name__ == '__main__':
    main()
