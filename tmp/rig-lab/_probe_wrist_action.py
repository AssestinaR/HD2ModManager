# -*- coding: utf-8 -*-
"""READ-ONLY: how does the real game drive l_hand vs l_hand_twist?

This is the measurement the expert asked for (brief 11.5).  l_hand and
l_hand_twist are both direct children of l_elbow with identical heads, so each
animation's rotation track for them *is* their rotation relative to the forearm.

For every player animation we
  * take the forearm axis from the animation's own initial local translation of
    l_hand (that vector lives in the elbow's frame, same frame as the tracks),
  * swing/twist decompose the delta rotation about that axis,
  * accumulate the range of twist and swing carried by each bone.

If l_hand_twist carries a large twist range while l_hand carries the swing, the
game really does split the wrist between the two bones and our forearm weights
should follow that split.  If l_hand_twist is static, it is useless to us.

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


def swing_twist(q, axis):
    """Return (twist_deg, swing_deg) of unit quaternion q about unit axis."""
    q = qnorm(q)
    a = qnorm(axis)
    d = q[0] * a[0] + q[1] * a[1] + q[2] * a[2]
    proj = [d * a[0], d * a[1], d * a[2]]
    tw = qnorm([proj[0], proj[1], proj[2], q[3]])
    if tw[3] < 0:
        tw = [-c for c in tw]
    sw = qmul(q, qconj(tw))
    if sw[3] < 0:
        sw = [-c for c in sw]
    twist = math.degrees(2.0 * math.acos(min(1.0, max(-1.0, tw[3]))))
    swing = math.degrees(2.0 * math.acos(min(1.0, max(-1.0, sw[3]))))
    # sign AFTER the w>0 flip, else q -> -q (same rotation) would flip the sign
    sign = tw[0] * a[0] + tw[1] * a[1] + tw[2] * a[2]
    return math.copysign(twist, sign if abs(sign) > 1e-12 else 1.0), swing


def bone_series(clip, idx):
    rest = qnorm(clip['initial'][idx][1])
    pts = [(0.0, rest)]
    pts += [(t / 1000.0, qnorm(v)) for t, v in T.tracks(clip, idx, 'rot')]
    return rest, pts


def analyse_clip(clip, idx_hand, idx_twist, idx_elbow, idx_shoulder):
    axis = clip['initial'][idx_hand][0]
    if math.sqrt(sum(c * c for c in axis)) < 1e-6:
        return None
    res = {}
    for tag, idx in (('hand', idx_hand), ('twist', idx_twist)):
        rest, pts = bone_series(clip, idx)
        tw_rng = [1e9, -1e9]
        sw_rng = [1e9, -1e9]
        max_step = 0.0
        prev = None
        for t, q in pts:
            tw, sw = swing_twist(qmul(qconj(rest), q), axis)
            tw_rng = [min(tw_rng[0], tw), max(tw_rng[1], tw)]
            sw_rng = [min(sw_rng[0], sw), max(sw_rng[1], sw)]
            if prev is not None:
                max_step = max(max_step, abs(tw - prev))
            prev = tw
        res[tag] = dict(twist=tw_rng[1] - tw_rng[0], swing=sw_rng[1] - sw_rng[0],
                        twist_abs=max(abs(tw_rng[0]), abs(tw_rng[1])),
                        swing_abs=max(abs(sw_rng[0]), abs(sw_rng[1])),
                        n=len(pts))
    # is the forearm axis actually the elbow->hand direction? (length sanity)
    res['axis_len'] = math.sqrt(sum(c * c for c in axis))
    res['length'] = clip['length']
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--archive', default=PLAYER_ARCHIVE)
    ap.add_argument('--limit', type=int, default=0)
    ap.add_argument('--top', type=int, default=15)
    args = ap.parse_args()

    con = sqlite3.connect('file:%s?mode=ro' % INDEX, uri=True)
    gd = G.GameData(GAME_DATA)
    r = con.execute('SELECT toc_data_offset,toc_data_size FROM archive_entries '
                    'WHERE type_id=? AND file_id=? AND archive_id=?',
                    (BONES_TYPE, s64(PLAYER_SKELETON), args.archive)).fetchone()
    names = T.parse_bones(gd.read_range(args.archive, r[0], r[1]))
    print('skeleton %s: %d bones' % (hex(PLAYER_SKELETON), len(names)))
    need = ('l_elbow', 'l_hand', 'l_hand_twist', 'r_elbow', 'r_hand', 'r_hand_twist')
    idx = {n: names.index(n) for n in need}
    print('indices: %s' % idx)

    rows = con.execute('SELECT file_id,toc_data_offset,toc_data_size FROM archive_entries '
                       'WHERE type_id=? AND archive_id=?',
                       (s64(ANIM_TYPE), args.archive)).fetchall()
    agg = collections.defaultdict(list)
    per_anim = []
    used = 0
    for fid, off, sz in rows:
        if args.limit and used >= args.limit:
            break
        d = gd.read_range(args.archive, off, sz)
        if len(d) < 32 or struct.unpack_from('<I', d, 4)[0] != len(names):
            continue
        for part, base in (('A', 0), ('B', None)):
            if base is None:
                a1 = T.parse_animation(d)
                base = a1['_end'] + 4
                if base + 32 > len(d):
                    continue
            clip = T.parse_animation(d[base:])
            if clip['bone_count'] != len(names):
                continue
            for side in ('l', 'r'):
                ih, it = idx[side + '_hand'], idx[side + '_hand_twist']
                ie, ish = idx[side + '_elbow'], None
                res = analyse_clip(clip, ih, it, ie, ish)
                if not res:
                    continue
                for tag in ('hand', 'twist'):
                    agg[side + '_' + tag + '_twist'].append(res[tag]['twist'])
                    agg[side + '_' + tag + '_swing'].append(res[tag]['swing'])
                per_anim.append((fid, part, side, res))
            used += 1

    def stat(v):
        if not v:
            return (0, 0, 0, 0)
        s = sorted(v)
        return (s[len(s) // 2], s[int(len(s) * 0.9)], s[-1], len(s))

    print('\nanimations analysed: %d clips, %d (bone,side) samples' % (used, len(per_anim)))
    print('%-16s %8s %8s %8s %8s' % ('metric', 'median', 'p90', 'max', 'n'))
    for k in ('l_hand_twist', 'l_hand_swing', 'l_twist_twist', 'l_twist_swing',
              'r_hand_twist', 'r_hand_swing', 'r_twist_twist', 'r_twist_swing'):
        med, p90, mx, n = stat(agg[k])
        print('%-16s %8.2f %8.2f %8.2f %8d' % (k, med, p90, mx, n))

    print('\ntop arm-active clips (by l_hand swing range):')
    ranked = sorted(per_anim, key=lambda x: -x[3]['hand']['swing'])[:args.top]
    print('%-20s %3s %2s %8s %8s %8s %8s %7s' % ('fileId', 'pt', 'sd', 'h_swing', 'h_twist', 't_swing', 't_twist', 'len'))
    for fid, part, side, res in ranked:
        if side != 'l':
            continue
        print('0x%016x %3s %2s %8.1f %8.1f %8.1f %8.1f %7.2f'
              % (fid & 0xFFFFFFFFFFFFFFFF, part, side, res['hand']['swing'], res['hand']['twist'],
                 res['twist']['swing'], res['twist']['twist'], res['length']))
    return 0


if __name__ == '__main__':
    sys.exit(main())
