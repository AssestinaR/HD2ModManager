# -*- coding: utf-8 -*-
"""Compare the two OUTPUTS (Lunalice vs Lilium) in HD2 space, hand/finger first.

The source-side probe (`_probe_src_arms.py`) says Lilium's arm is 40.8 mm shorter
than Lunalice's.  That is a fact about the *inputs*.  What actually matters is
where the hand ends up in the *output* -- and that is a different question,
because the shipped skeleton is HD2's either way.

So this probe works on `out/*/model.npz`:

  * `b_head` must be identical for both builds (the "skeleton never moves" rule)
  * `w_bone`/`w_val` group the vertices per HD2 bone, so each bone's vertex cloud
    can be compared directly: count, centroid, bbox.

usage: _probe_out_hands.py
"""
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

RUNS = [('Lunalice', 'out/blender'), ('Lilium  ', 'out/lilium')]


def load(d):
    z = np.load(os.path.join(HERE, d, 'model.npz'))
    meta = json.load(open(os.path.join(HERE, d, 'meta.json'), encoding='utf-8'))
    return dict(bind=np.asarray(z['ours_bind'], np.float64),
                tpose=np.asarray(z['src_tpose'], np.float64),
                wb=np.asarray(z['w_bone'], np.int64),
                wv=np.asarray(z['w_val'], np.float64),
                bhead=np.asarray(z['b_head'], np.float64),
                blen=np.asarray(z['b_len'], np.float64),
                names=meta['bones'])


def primary(d):
    """-> array of the dominant bone index per vertex (ties -> lowest index)."""
    wb, wv = d['wb'], d['wv']
    j = np.argmax(wv, axis=1)
    return wb[np.arange(len(wb)), j]


def group(d, pred):
    p = primary(d)
    m = np.array([pred(d['names'][i]) for i in range(len(d['names']))])
    sel = m[p]
    return sel, p


def stats(pts):
    if len(pts) == 0:
        return None
    c = pts.mean(0)
    lo, hi = pts.min(0), pts.max(0)
    return dict(n=len(pts), c=c, lo=lo, hi=hi)


def main():
    A, B = load(RUNS[0][1]), load(RUNS[1][1])
    print('=== 骨架不动核对（b_head / b_len 必须逐项相同）===')
    for k in ('bhead', 'blen'):
        same = np.array_equal(A[k], B[k])
        d = float(np.abs(A[k] - B[k]).max())
        print('  %-8s identical=%-5s  max|delta|=%.3e' % (k, same, d))
    print('  bones: %d vs %d' % (len(A['names']), len(B['names'])))
    print()

    is_finger = lambda nm: 'finger' in nm
    for tag, d in ((RUNS[0][0], A), (RUNS[1][0], B)):
        sel, p = group(d, is_finger)
        pts = d['bind'][sel]
        s = stats(pts)
        print('%s 手指骨驱动的顶点 %d 个  质心 (%7.4f,%7.4f,%7.4f)'
              % (tag, s['n'], s['c'][0], s['c'][1], s['c'][2]))
    print()

    print('=== 逐 HD2 手部骨：顶点云（HD2 空间；dist = 到该骨头部的平均距离，mm）===')
    names = [nm for nm in A['names'] if 'finger' in nm or nm.endswith('_hand')]
    print('  %-22s %6s %6s %8s %8s   %-22s %-22s %8s'
          % ('bone', 'nA', 'nB', 'distA', 'distB', 'Lunalice centroid',
             'Lilium centroid', 'delta'))
    rows = []
    for nm in names:
        i = A['names'].index(nm)
        pa = A['bind'][primary(A) == i]
        pb = B['bind'][primary(B) == i]
        if len(pa) == 0 or len(pb) == 0:
            print('  %-22s %6d %6d   (one side empty)' % (nm, len(pa), len(pb)))
            continue
        ca, cb = pa.mean(0), pb.mean(0)
        da = float(np.linalg.norm(pa - A['bhead'][i], axis=1).mean()) * 1000
        db = float(np.linalg.norm(pb - B['bhead'][i], axis=1).mean()) * 1000
        dd = float(np.linalg.norm(cb - ca)) * 1000
        rows.append((dd, nm))
        print('  %-22s %6d %6d %8.1f %8.1f   (%7.4f,%7.4f,%7.4f) (%7.4f,%7.4f,%7.4f) %7.1f'
              % (nm, len(pa), len(pb), da, db, ca[0], ca[1], ca[2],
                 cb[0], cb[1], cb[2], dd))
    rows.sort(reverse=True)
    print('  --- 最大 %s %.1f mm / 中位 %.1f mm ---'
          % (rows[0][1], rows[0][0], np.median([r[0] for r in rows])))
    print()

    # which source bone do the runaway vertices come from?
    print('=== 异常组的顶点到底在哪 ===')
    for nm in ('l_pinky_finger1', 'l_index_finger1', 'l_hand'):
        i = A['names'].index(nm)
        for tag, d in ((RUNS[0][0], A), (RUNS[1][0], B)):
            pts = d['bind'][primary(d) == i]
            if len(pts) == 0:
                continue
            lo, hi = pts.min(0), pts.max(0)
            far = float(np.linalg.norm(pts - d['bhead'][i], axis=1).max()) * 1000
            print('  %-18s %s n=%-5d bbox X=[%7.3f,%7.3f] Y=[%7.3f,%7.3f] '
                  'Z=[%7.3f,%7.3f]  max dist from bone head %8.1f mm'
                  % (nm, tag, len(pts), lo[0], hi[0], lo[1], hi[1], lo[2], hi[2],
                     far))
    print()

    # the whole model, as the scale reference: is Lilium uniformly smaller?
    print('=== 整体参考 ===')
    for tag, d in ((RUNS[0][0], A), (RUNS[1][0], B)):
        v = d['bind']
        print('  %s  bind bbox  X=[%.3f,%.3f] Y=[%.3f,%.3f] Z=[%.3f,%.3f]  '
              'height=%.4f' % (tag, v[:, 0].min(), v[:, 0].max(),
                               v[:, 1].min(), v[:, 1].max(),
                               v[:, 2].min(), v[:, 2].max(),
                               v[:, 2].max() - v[:, 2].min()))
    return 0


if __name__ == '__main__':
    sys.exit(main())
