# -*- coding: utf-8 -*-
"""READ-ONLY: dump the raw signed twist / swing series of one clip.

Two questions from _probe_wrist_split.py:
  * is a 268 deg hand twist range real motion or an unwrap artefact?
  * why do two clips show a *negative* hand~twist correlation?
Also prints the player skeleton bone list around the arm so we can see what
index 16 (between l_elbow=15 and l_hand=17) actually is.

Nothing is written.
"""
import argparse
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


def sts(q, axis):
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


def series(clip, idx, axis):
    rest = qnorm(clip['initial'][idx][1])
    pts = [(0.0, rest)] + [(t / 1000.0, qnorm(v)) for t, v in T.tracks(clip, idx, 'rot')]
    return [(t, *sts(qmul(qconj(rest), q), axis)) for t, q in pts]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--dump', default='0x886815dffcee82ac',
                    help='file id (hex) of the clip to dump')
    ap.add_argument('--part', default='A')
    args = ap.parse_args()

    con = sqlite3.connect('file:%s?mode=ro' % INDEX, uri=True)
    gd = G.GameData(GAME_DATA)
    r = con.execute('SELECT toc_data_offset,toc_data_size FROM archive_entries '
                    'WHERE type_id=? AND file_id=? AND archive_id=?',
                    (BONES_TYPE, s64(PLAYER_SKELETON), PLAYER_ARCHIVE)).fetchone()
    names = T.parse_bones(gd.read_range(PLAYER_ARCHIVE, r[0], r[1]))
    print('--- player skeleton, indices 8..24 and 70..80 ---')
    for i in list(range(8, 25)) + list(range(70, 81)):
        print('  %2d  %s' % (i, names[i]))

    fid = int(args.dump, 16)
    row = con.execute('SELECT toc_data_offset,toc_data_size FROM archive_entries '
                      'WHERE type_id=? AND file_id=? AND archive_id=?',
                      (s64(ANIM_TYPE), s64(fid), PLAYER_ARCHIVE)).fetchone()
    if not row:
        print('no such animation 0x%016x' % fid)
        return 1
    d = gd.read_range(PLAYER_ARCHIVE, row[0], row[1])
    base = 0
    if args.part == 'B':
        base = T.parse_animation(d)['_end'] + 4
    clip = T.parse_animation(d[base:])
    idx = {n: names.index(n) for n in ('l_elbow', 'l_hand', 'l_hand_twist')}
    axis = clip['initial'][idx['l_hand']][0]
    print('\nclip 0x%016x part %s: len=%.2f s  bones=%d  entries=%d'
          % (fid, args.part, clip['length'], clip['bone_count'], len(clip['entries'])))
    print('l_elbow rot tracks=%d  l_hand=%d  l_hand_twist=%d'
          % (len(T.tracks(clip, idx['l_elbow'], 'rot')),
             len(T.tracks(clip, idx['l_hand'], 'rot')),
             len(T.tracks(clip, idx['l_hand_twist'], 'rot'))))
    sh = series(clip, idx['l_hand'], axis)
    st = series(clip, idx['l_hand_twist'], axis)
    se = series(clip, idx['l_elbow'], [1.0, 0.0, 0.0])
    print('\n%7s %10s %8s | %10s %8s | %10s' % ('t(s)', 'hand tw', 'hand sw',
                                                 'twist tw', 'twist sw', 'elbow tot'))
    grid = sorted(set([x[0] for x in sh] + [x[0] for x in st] + [x[0] for x in se]))
    for g in grid:
        h = min(sh, key=lambda x: abs(x[0] - g))
        t = min(st, key=lambda x: abs(x[0] - g))
        e = min(se, key=lambda x: abs(x[0] - g))
        print('%7.3f %10.1f %8.1f | %10.1f %8.1f | %10.1f'
              % (g, h[1], h[2], t[1], t[2], e[1]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
