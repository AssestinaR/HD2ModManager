# -*- coding: utf-8 -*-
"""READ-ONLY: how is the wrist split between l_hand and l_hand_twist?

Follow-up to _probe_wrist_action.py.  l_hand and l_hand_twist are siblings under
l_elbow with coincident heads, so the only thing that can differ is their
animated rotation.  This probe

  1. verifies the "twin bone" claim (identical rest translation AND rotation),
  2. per clip, decomposes both bones' delta rotation into a signed twist about
     the forearm axis plus a swing magnitude,
  3. measures whether the twist bone is driven as a *fraction* of the hand
     (peak-to-peak ratio, time-domain correlation),
  4. reports the elbow's own rotation range for context.

If the twist bone tracks the hand at a stable fraction, it is a twist helper and
our forearm weights should follow that fraction.  If it is static, it is
decoration and can be ignored.

Nothing is written.
"""
import argparse
import collections
import math
import os
import sqlite3
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_gamedata as G
import _probe_anim_tracks as T

ANIM_TYPE = 0x931E336D7646CC26
BONES_TYPE = 0x18DEAD01056B72E9
PLAYER_SKELETON = 0x4D1C334D294DFA97
PLAYER_ARCHIVE = '18235e0c9ec0e636'
INDEX = 'E:/tmp/hd2-mgroot/data/indexes/asset-index.sqlite'
GAME_DATA = 'D:/SteamLibrary/steamapps/common/Helldivers 2/data'


def s64(x):
    return x - (1 << 64) if x >= (1 << 63) else x


def qnorm(q):
    n = math.sqrt(sum(c * c for c in q)) or 1.0
    return [c / n for c in q]


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return [aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz]


def qconj(q):
    return [-q[0], -q[1], -q[2], q[3]]


def signed_twist_swing(q, axis):
    """(signed twist deg, swing deg) of unit q about unit axis."""
    q = qnorm(q)
    a = qnorm(axis)
    d = q[0] * a[0] + q[1] * a[1] + q[2] * a[2]
    tw = qnorm([d * a[0], d * a[1], d * a[2], q[3]])
    if tw[3] < 0:
        tw = [-c for c in tw]
    sw = qmul(q, qconj(tw))
    if sw[3] < 0:
        sw = [-c for c in sw]
    ang = math.degrees(2.0 * math.acos(min(1.0, max(-1.0, tw[3]))))
    swing = math.degrees(2.0 * math.acos(min(1.0, max(-1.0, sw[3]))))
    # sign AFTER the w>0 flip, else q -> -q (same rotation) would flip the sign
    sign = tw[0] * a[0] + tw[1] * a[1] + tw[2] * a[2]
    return math.copysign(ang, sign if abs(sign) > 1e-12 else 1.0), swing


def rot_series(clip, idx, axis):
    rest = qnorm(clip['initial'][idx][1])
    pts = [(0.0, rest)]
    pts += [(t / 1000.0, qnorm(v)) for t, v in T.tracks(clip, idx, 'rot')]
    out = []
    for t, q in pts:
        tw, sw = signed_twist_swing(qmul(qconj(rest), q), axis)
        out.append((t, tw, sw))
    return out


def total_angle(rest, q):
    d = qmul(qconj(qnorm(rest)), qnorm(q))
    return math.degrees(2.0 * math.acos(min(1.0, max(-1.0, abs(d[3])))))


def interp_unwrap(series, grid):
    """linear interp of the signed twist onto grid, unwrapped."""
    ts = [s[0] for s in series]
    vs = [s[1] for s in series]
    if len(ts) == 1:
        return [vs[0]] * len(grid)
    u = [vs[0]]
    for i in range(1, len(vs)):
        d = vs[i] - vs[i - 1]
        while d > 180.0:
            d -= 360.0
        while d < -180.0:
            d += 360.0
        u.append(u[-1] + d)
    out = []
    for g in grid:
        if g <= ts[0]:
            out.append(u[0])
        elif g >= ts[-1]:
            out.append(u[-1])
        else:
            j = 0
            while j + 1 < len(ts) and ts[j + 1] < g:
                j += 1
            f = (g - ts[j]) / (ts[j + 1] - ts[j]) if ts[j + 1] > ts[j] else 0.0
            out.append(u[j] + f * (u[j + 1] - u[j]))
    return out


def pearson(a, b):
    n = len(a)
    if n < 3:
        return float('nan')
    ma = sum(a) / n
    mb = sum(b) / n
    va = sum((x - ma) ** 2 for x in a)
    vb = sum((x - mb) ** 2 for x in b)
    if va < 1e-12 or vb < 1e-12:
        return float('nan')
    return sum((a[i] - ma) * (b[i] - mb) for i in range(n)) / math.sqrt(va * vb)


def rng(v):
    return max(v) - min(v) if v else 0.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--archive', default=PLAYER_ARCHIVE)
    ap.add_argument('--limit', type=int, default=0)
    ap.add_argument('--top', type=int, default=20)
    args = ap.parse_args()

    con = sqlite3.connect('file:%s?mode=ro' % INDEX, uri=True)
    gd = G.GameData(GAME_DATA)
    r = con.execute('SELECT toc_data_offset,toc_data_size FROM archive_entries '
                    'WHERE type_id=? AND file_id=? AND archive_id=?',
                    (BONES_TYPE, s64(PLAYER_SKELETON), args.archive)).fetchone()
    names = T.parse_bones(gd.read_range(args.archive, r[0], r[1]))
    idx = {n: names.index(n) for n in
           ('l_elbow', 'l_hand', 'l_hand_twist', 'r_elbow', 'r_hand', 'r_hand_twist')}
    print('skeleton: %d bones  idx=%s' % (len(names), idx))

    rows = con.execute('SELECT file_id,toc_data_offset,toc_data_size FROM archive_entries '
                       'WHERE type_id=? AND archive_id=?',
                       (s64(ANIM_TYPE), args.archive)).fetchall()

    twin_ok = twin_bad = 0
    per = []
    used = 0
    for fid, off, sz in rows:
        if args.limit and used >= args.limit:
            break
        d = gd.read_range(args.archive, off, sz)
        if len(d) < 32 or struct.unpack_from('<I', d, 4)[0] != len(names):
            continue
        for part, base in (('A', 0), ('B', None)):
            if base is None:
                base = T.parse_animation(d)['_end'] + 4
                if base + 32 > len(d):
                    continue
            clip = T.parse_animation(d[base:])
            if clip['bone_count'] != len(names):
                continue
            used += 1
            for side in ('l', 'r'):
                ih, it, ie = idx[side + '_hand'], idx[side + '_hand_twist'], idx[side + '_elbow']
                axis = clip['initial'][ih][0]
                if math.sqrt(sum(c * c for c in axis)) < 1e-6:
                    continue
                if (clip['initial'][ih][0] == clip['initial'][it][0]
                        and clip['initial'][ih][1] == clip['initial'][it][1]):
                    twin_ok += 1
                else:
                    twin_bad += 1
                sh = rot_series(clip, ih, axis)
                st = rot_series(clip, it, axis)
                # elbow total rotation
                erest = clip['initial'][ie][1]
                epk = 0.0
                for t, q in [(0.0, qnorm(erest))] + [(t / 1000.0, qnorm(v))
                                                     for t, v in T.tracks(clip, ie, 'rot')]:
                    epk = max(epk, total_angle(erest, q))
                grid = sorted(set([s[0] for s in sh] + [s[0] for s in st]))
                gh = interp_unwrap(sh, grid)
                gt = interp_unwrap(st, grid)
                hand_tw = rng([s[1] for s in sh])
                tw_tw = rng([s[1] for s in st])
                hand_sw = rng([s[2] for s in sh])
                per.append(dict(fid=fid, part=part, side=side, hand_tw=hand_tw, tw_tw=tw_tw,
                                hand_sw=hand_sw, elbow=epk, length=clip['length'],
                                ratio=(tw_tw / hand_tw if hand_tw > 1e-6 else float('nan')),
                                corr=pearson(gh, gt), axis=axis))

    print('twin-bone rest identical: %d yes / %d no' % (twin_ok, twin_bad))
    if per:
        a0 = per[0]['axis']
        print('forearm axis (elbow frame, left) = [%.4f %.4f %.4f]  len=%.4f m'
              % (a0[0], a0[1], a0[2], math.sqrt(sum(c * c for c in a0))))

    def stat(v, name, fmt='%8.2f'):
        v = [x for x in v if not (isinstance(x, float) and math.isnan(x))]
        if not v:
            print('%-22s (none)' % name)
            return
        s = sorted(v)
        print(('%-22s ' + fmt + ' ' + fmt + ' ' + fmt + ' %6d')
              % (name, s[len(s) // 2], s[int(len(s) * 0.9)], s[-1], len(s)))

    for side in ('l', 'r'):
        sel = [p for p in per if p['side'] == side]
        print('\n--- %s side (%d samples) ---' % (side.upper(), len(sel)))
        stat([p['hand_tw'] for p in sel], 'hand twist range (deg)')
        stat([p['tw_tw'] for p in sel], 'twist bone range (deg)')
        stat([p['hand_sw'] for p in sel], 'hand swing range (deg)')
        stat([p['elbow'] for p in sel], 'elbow rot peak (deg)')
        act = [p for p in sel if p['hand_tw'] > 20.0]
        stat([p['ratio'] for p in act], 'ratio tw/hand (hand>20)')
        stat([p['corr'] for p in act], 'corr tw~hand (hand>20)')
        print('  clips with hand twist > 20 deg: %d / %d' % (len(act), len(sel)))

    print('\ntop clips by hand twist range (left):')
    ranked = sorted([p for p in per if p['side'] == 'l'], key=lambda x: -x['hand_tw'])[:args.top]
    print('%-20s %3s %7s %7s %7s %6s %7s %6s'
          % ('fileId', 'pt', 'h_twist', 't_twist', 'ratio', 'corr', 'h_swing', 'elbow'))
    for p in ranked:
        print('0x%016x %3s %7.1f %7.1f %7.2f %6.2f %7.1f %6.1f'
              % (p['fid'] & 0xFFFFFFFFFFFFFFFF, p['part'], p['hand_tw'], p['tw_tw'],
                 p['ratio'] if not math.isnan(p['ratio']) else -1.0,
                 p['corr'] if not math.isnan(p['corr']) else -9.0,
                 p['hand_sw'], p['elbow']))
    return 0


if __name__ == '__main__':
    sys.exit(main())
