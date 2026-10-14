# -*- coding: utf-8 -*-
"""READ-ONLY probe: parse a Stingray animation resource header.

Verifies the two facts we need before we can extract real arm motion:
  * the bone hash table in the animation is MurmurHash64A of the bone name;
  * bone_count / hashes_count line up with the player skeleton.

Usage:
    _probe_anim.py <animation_*.toc> [--bones <meta.json with a 'bones' list>]
"""
import argparse
import json
import math
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_hash as H


def bits_from_bytes(raw, count):
    """bit i = (raw[i//8] >> (i % 8)) & 1   (SDK double-reversal collapses)."""
    return [(raw[i >> 3] >> (i & 7)) & 1 for i in range(count)]


def parse(path):
    d = open(path, 'rb').read()
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

    a = {}
    a['unk'] = u32()
    a['bone_count'] = u32()
    a['animation_length'] = f32()
    a['file_size'] = u32()
    a['hashes_count'] = u32()
    a['hashes2_count'] = u32()
    a['hashes'] = [u64() for _ in range(a['hashes_count'])]
    a['hashes2'] = [u64() for _ in range(a['hashes2_count'])]
    a['unk2'] = u16()
    nbytes = math.ceil(3 * a['bone_count'] / 8)
    if nbytes % 2:
        nbytes += 1
    raw = d[o:o + nbytes]
    o += nbytes
    bits = bits_from_bytes(raw, 3 * a['bone_count'])
    a['initial'] = []
    for i in range(a['bone_count']):
        pos_c, rot_c, scl_c = bits[i * 3], bits[i * 3 + 1], bits[i * 3 + 2]
        pos = [u16() for _ in range(3)] if pos_c else [f32() for _ in range(3)]
        rot = [u32()] if rot_c else [f32() for _ in range(4)]
        scl = [u16() for _ in range(3)] if scl_c else [f32() for _ in range(3)]
        a['initial'].append(dict(compress=(pos_c, rot_c, scl_c), position=pos,
                                 rotation=rot, scale=scl))
    a['hashes_floats'] = [f32() for _ in range(a['hashes_count'])]
    a['_offset_after_header'] = o
    a['_file_size_bytes'] = len(d)
    return a


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('anim')
    ap.add_argument('--bones', default='')
    ap.add_argument('--show', type=int, default=12)
    args = ap.parse_args()

    a = parse(args.anim)
    print('file            %s (%d bytes)' % (os.path.basename(args.anim), a['_file_size_bytes']))
    print('unk=%d  unk2=%d  bone_count=%d  hashes_count=%d  hashes2_count=%d'
          % (a['unk'], a['unk2'], a['bone_count'], a['hashes_count'], a['hashes2_count']))
    print('animation_length=%.4f  file_size_field=%d  header_ends_at=%d'
          % (a['animation_length'], a['file_size'], a['_offset_after_header']))
    cpos = sum(1 for b in a['initial'] if b['compress'][0])
    crot = sum(1 for b in a['initial'] if b['compress'][1])
    cscl = sum(1 for b in a['initial'] if b['compress'][2])
    print('initial states compressed (pos/rot/scale): %d / %d / %d' % (cpos, crot, cscl))
    print('first %d hashes:' % args.show)
    for i, h in enumerate(a['hashes'][:args.show]):
        print('   [%2d] 0x%016x  low32=0x%08x' % (i, h, h & 0xFFFFFFFF))

    if args.bones and os.path.isfile(args.bones):
        m = json.load(open(args.bones, encoding='utf-8'))
        names = m['bones'] if isinstance(m, dict) and 'bones' in m else m
        h64 = {H.murmur64_hash(n.encode()): n for n in names}
        h32 = {H.name_hash(n.encode()): n for n in names}
        hit64 = [h64[h] for h in a['hashes'] if h in h64]
        hit32 = [h32[h & 0xFFFFFFFF] for h in a['hashes'] if (h & 0xFFFFFFFF) in h32]
        print('reference bone names: %d' % len(names))
        print('animation hashes matched by FULL murmur64 : %d' % len(hit64))
        print('animation hashes matched by low-32 only   : %d' % len(hit32))
        if hit64:
            print('   matched (full): %s' % ', '.join(sorted(set(hit64))[:20]))
        if hit32:
            print('   matched (low32): %s' % ', '.join(sorted(set(hit32))[:20]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
