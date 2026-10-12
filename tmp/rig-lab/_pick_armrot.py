"""Round 45c: for a few --arm-rot candidates print BOTH the frozen-selection and
the live-geometry direction fits, so the chosen value is a fair compromise.

usage: python _pick_armrot.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import probe_arm_angle as PA

CFGS = [(None, 'off'),
        ((8.90, -15.75, 19.82), 'frozen fit'),
        ((10.92, -18.75, 23.10), 'live fit'),
        ((9.90, -17.25, 21.50), 'midpoint')]
SEGS = ('upper arm', 'forearm', 'hand')


def main():
    base = PA.run(None)
    _, tsel, _, _ = PA.arm_cloud(base)
    ref = PA.measure(base, quiet=True)['ref']
    print('ref        ' + '  '.join('%s %6.2f' % (s, ref[s][0]) for s in SEGS)
          + '   |  ' + '  '.join('%s %+6.2f' % (s, ref[s][1]) for s in SEGS))
    print('%-11s %-27s | %-27s | elbow' % ('arm-rot', 'X-Z  frozen / live', 'X-Y  frozen / live'))
    for rot, tag in CFGS:
        r = base if rot is None else PA.run(rot)
        F = PA.measure(r, quiet=True, tsel=tsel)
        L = PA.measure(r, quiet=True)
        print('%-11s %s | %s | %s'
              % (tag,
                 ' / '.join('%6.2f' % F['ours'][s][0] for s in SEGS),
                 ' / '.join('%6.2f' % L['ours'][s][0] for s in SEGS),
                 ' | '.join('%+6.2f' % L['ours'][s][1] for s in SEGS)))
        print('%-11s %s | %s | bend %.2f' % (
            '', ' '.join('%6.2f' % (F['ours'][s][0] - ref[s][0]) for s in SEGS),
            ' '.join('%6.2f' % (L['ours'][s][0] - ref[s][0]) for s in SEGS),
            L['elbow']['ours']))


if __name__ == '__main__':
    main()
