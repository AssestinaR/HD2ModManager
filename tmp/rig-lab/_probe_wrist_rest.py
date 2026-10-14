# -*- coding: utf-8 -*-
"""READ-ONLY: what exactly is l_hand_twist's rest relation to l_hand / l_elbow?

_probe_wrist_split.py reported that the animation 'initial' state of l_hand and
l_hand_twist differ in 570 of 580 samples, yet the twist decomposition about the
l_hand local translation gives exactly zero swing for the twist bone.  Those two
facts together decide whether the twist bone is

  (a) a sibling of l_hand under l_elbow (coincident heads) - its rotation is an
      alternative orientation for the same wrist point, or
  (b) a child of l_hand with a zero local offset - its rotation *composes on top
      of* the hand's, so the mesh sees hand_rot * twist_rot.

We print the raw initial (pos, rot, scale) of l_elbow / l_hand / l_hand_twist for
several clips and also the per-clip variation of each.

Nothing is written.
"""
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


def main():
    con = sqlite3.connect('file:%s?mode=ro' % INDEX, uri=True)
    gd = G.GameData(GAME_DATA)
    r = con.execute('SELECT toc_data_offset,toc_data_size FROM archive_entries '
                    'WHERE type_id=? AND file_id=? AND archive_id=?',
                    (BONES_TYPE, s64(PLAYER_SKELETON), PLAYER_ARCHIVE)).fetchone()
    names = T.parse_bones(gd.read_range(PLAYER_ARCHIVE, r[0], r[1]))
    idx = {n: names.index(n) for n in
           ('l_elbow', 'l_hand', 'l_hand_twist', 'r_hand', 'r_hand_twist',
            'attach_hand_l', 'attach_hand_r')}
    print('idx=%s' % idx)

    rows = con.execute('SELECT file_id,toc_data_offset,toc_data_size FROM archive_entries '
                       'WHERE type_id=? AND archive_id=?',
                       (s64(ANIM_TYPE), PLAYER_ARCHIVE)).fetchall()

    def fmt(v):
        return '[' + ' '.join('%9.5f' % x for x in v) + ']'

    seen = 0
    variants = collections.Counter()
    posvar = collections.defaultdict(set)
    for fid, off, sz in rows:
        if seen >= 12:
            break
        d = gd.read_range(PLAYER_ARCHIVE, off, sz)
        if len(d) < 32 or struct.unpack_from('<I', d, 4)[0] != len(names):
            continue
        clip = T.parse_animation(d)
        if clip['bone_count'] != len(names):
            continue
        seen += 1
        ih, it, ie = idx['l_hand'], idx['l_hand_twist'], idx['l_elbow']
        print('\nclip 0x%016x' % (fid & 0xFFFFFFFFFFFFFFFF))
        for tag in ('l_elbow', 'l_hand', 'l_hand_twist'):
            i = idx[tag]
            p, q, s = clip['initial'][i]
            print('  %-13s pos=%s rot=%s scl=%s' % (tag, fmt(p), fmt(q), fmt(s)))
        print('  pos(l_hand)==pos(l_hand_twist)? %s   rot equal? %s'
              % (clip['initial'][ih][0] == clip['initial'][it][0],
                 clip['initial'][ih][1] == clip['initial'][it][1]))
        for tag in ('l_elbow', 'l_hand', 'l_hand_twist'):
            posvar[tag].add(tuple(round(x, 6) for x in clip['initial'][idx[tag]][0]))
        variants[tuple(round(x, 6) for x in clip['initial'][it][0])] += 1

    print('\n--- across %d clips ---' % seen)
    for tag in ('l_elbow', 'l_hand', 'l_hand_twist'):
        print('  %-13s distinct initial positions: %d  %s'
              % (tag, len(posvar[tag]),
                 '; '.join(fmt(list(v)) for v in list(posvar[tag])[:4])))
    return 0


if __name__ == '__main__':
    sys.exit(main())
