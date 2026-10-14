# -*- coding: utf-8 -*-
"""READ-ONLY decoder for HD2 (Stingray) animation resources.

Ports docs/HD2SDK-CommunityEdition-3.8.0/stingray/animation.py to plain Python
(the SDK module imports bpy) and adds the per-bone track extraction we need.

Key facts established this round:
  * a resource's file id is MurmurHash64A of its content path (verified against
    friendlynames.txt);
  * animation bone index i refers to bone i of the bound `bones` resource, and
    the player skeleton is bones 0x4d1c334d294dfa97 (90 names, includes
    l_hand_twist / r_hand_twist);
  * animations carry no name table, so the binding is by index.

Usage:
    _probe_anim_tracks.py <animation.toc> [--bones <bones.toc>]
"""
import argparse
import math
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))


def decompress_rotation(r):
    first = (((r & 0xFFC) >> 2) - 512.0) / 512.0 * 0.75
    second = (((r & 0x3FF000) >> 12) - 512.0) / 512.0 * 0.75
    third = (((r & 0xFFC00000) >> 22) - 512.0) / 512.0 * 0.75
    largest_idx = r & 0x3
    v = math.sqrt(max(0.0, 1.0 - third * third - second * second - first * first))
    if largest_idx == 0:
        return [v, first, second, third]
    if largest_idx == 1:
        return [third, v, first, second]
    if largest_idx == 2:
        return [second, third, v, first]
    return [first, second, third, v]


def decompress_position(p):
    return [(x - 32767.0) * (10.0 / 32767.0) for x in p]


def parse_bones(data):
    n = struct.unpack_from('<I', data, 0)[0]
    nl = struct.unpack_from('<I', data, 4)[0]
    o = 8 + 4 * nl + 4 * n + 4 * nl
    names = [x.decode('utf-8', 'replace') for x in data[o:].split(b'\x00')]
    if names and names[-1] == '':
        names.pop()
    return names


def parse_animation(d):
    o = 0

    def u32():
        nonlocal o
        v = struct.unpack_from('<I', d, o)[0]; o += 4; return v

    def u16():
        nonlocal o
        v = struct.unpack_from('<H', d, o)[0]; o += 2; return v

    def u64():
        nonlocal o
        v = struct.unpack_from('<Q', d, o)[0]; o += 8; return v

    def f32():
        nonlocal o
        v = struct.unpack_from('<f', d, o)[0]; o += 4; return v

    a = {'unk': u32()}
    a['bone_count'] = u32()
    a['length'] = f32()
    a['file_size'] = u32()
    hc = u32(); hc2 = u32()
    a['hashes'] = [u64() for _ in range(hc)]
    a['hashes2'] = [u64() for _ in range(hc2)]
    a['unk2'] = u16()
    nbytes = math.ceil(3 * a['bone_count'] / 8)
    if nbytes % 2:
        nbytes += 1
    raw = d[o:o + nbytes]; o += nbytes
    bits = [(raw[i >> 3] >> (i & 7)) & 1 for i in range(3 * a['bone_count'])]
    a['initial'] = []
    for i in range(a['bone_count']):
        pc, rc, sc = bits[i * 3], bits[i * 3 + 1], bits[i * 3 + 2]
        if pc:
            pos = decompress_position([u16() for _ in range(3)])
        else:
            pos = [f32() for _ in range(3)]
        rot = decompress_rotation(u32()) if rc else [f32() for _ in range(4)]
        if sc:
            scl = decompress_position([u16() for _ in range(3)])
        else:
            scl = [f32() for _ in range(3)]
        a['initial'].append((pos, rot, scl))
    a['hashes_floats'] = [f32() for _ in range(hc)]
    a['_header_end'] = o

    entries = []
    guard = 0
    while o + 2 <= len(d):
        guard += 1
        if guard > 2000000:
            break
        if struct.unpack_from('<H', d, o)[0] == 3:
            o += 2
            break
        data = d[o:o + 4]
        etype = (data[1] & 0xC0) >> 6
        if etype == 0:
            subtype = struct.unpack_from('<H', d, o)[0]; o += 2
            if subtype == 3:
                break
            bone = struct.unpack_from('<I', d, o)[0]; o += 4
            time = struct.unpack_from('<f', d, o)[0] * 1000.0; o += 4
        else:
            subtype = 0
            bone = ((data[0] & 0xF0) >> 4) | ((data[1] & 0x3F) << 4)
            time = ((data[0] & 0xF) << 16) | (data[3] << 8) | data[2]
            o += 4
        if etype == 3:
            val = decompress_rotation(struct.unpack_from('<I', d, o)[0]); o += 4
            kind = 'rot'
        elif etype == 2:
            val = decompress_position([struct.unpack_from('<H', d, o + 2 * k)[0] for k in range(3)]); o += 6
            kind = 'pos'
        elif etype == 1:
            val = decompress_position([struct.unpack_from('<H', d, o + 2 * k)[0] for k in range(3)]); o += 6
            kind = 'scl'
        else:
            if subtype == 4:
                val = [struct.unpack_from('<f', d, o + 4 * k)[0] for k in range(3)]; o += 12; kind = 'pos'
            elif subtype == 5:
                val = [struct.unpack_from('<f', d, o + 4 * k)[0] for k in range(4)]; o += 16; kind = 'rot'
            elif subtype == 6:
                val = [struct.unpack_from('<f', d, o + 4 * k)[0] for k in range(3)]; o += 12; kind = 'scl'
            elif subtype == 2:
                val = None; kind = 'trig'
            else:
                kind = 'unknown'; val = None
                a['stop'] = ('unknown subtype/type', etype, subtype, o)
                break
        if not (etype == 0 and subtype not in (2, 4, 5, 6)):
            entries.append(dict(kind=kind, bone=bone, time=time, value=val))
    a['entries'] = entries
    a['_end'] = o
    return a


def tracks(anim, bone, kind='rot'):
    return [(e['time'], e['value']) for e in anim['entries']
            if e['bone'] == bone and e['kind'] == kind and e['value'] is not None]


def quat_mul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return [aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz]


def quat_conj(q):
    return [-q[0], -q[1], -q[2], q[3]]


def quat_angle(q):
    w = min(1.0, max(-1.0, abs(q[3])))
    return math.degrees(2.0 * math.acos(w))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('anim')
    ap.add_argument('--bones', default='')
    args = ap.parse_args()

    a = parse_animation(open(args.anim, 'rb').read())
    print('bone_count=%d  length=%.3f s  file_size=%d  consumed=%d/%d  entries=%d'
          % (a['bone_count'], a['length'], a['file_size'], a['_end'],
             len(open(args.anim, 'rb').read()), len(a['entries'])))
    print('initial rotation of bone 0: %s' % ['%.4f' % x for x in a['initial'][0][1]])
    print('initial rotation of bone 1: %s' % ['%.4f' % x for x in a['initial'][1][1]])
    if args.bones:
        names = parse_bones(open(args.bones, 'rb').read())
        print('skeleton names: %d' % len(names))
        for nm in ('l_elbow', 'l_hand', 'l_hand_twist', 'r_elbow', 'r_hand', 'r_hand_twist'):
            if nm in names:
                i = names.index(nm)
                r = tracks(a, i, 'rot')
                print('  %-14s idx=%2d  rot tracks=%d  first=%s'
                      % (nm, i, len(r), ['%.4f' % x for x in (r[0][1] if r else a['initial'][i][1])]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
