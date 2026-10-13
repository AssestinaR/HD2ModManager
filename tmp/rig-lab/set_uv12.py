# -*- coding: utf-8 -*-
"""Set uv1/uv2 to the constant (0, 1) that the working reference ships.

Read-only on the source patch; writes a new candidate directory.

Why: `_probe_uvch.py` compared the two armour units we can actually read.

    reference 【4.28】Chiffon-LUNALICE替换RE-2310   (renders correctly in game)
        uv0 = real UVs, all inside 0..1
        uv1 = (0, 1) for every vertex          <- constant
        uv2 = (0, 1) for every vertex          <- constant

    ours (M4W)
        uv0 = uv1 = uv2 = the SOURCE UVs, including -21.06 .. 22.98

`pack_mesh_vertices` deliberately copied one UV set into all three channels.
Whatever the game reads uv1/uv2 for, a known-good HD2 armour unit keeps them
constant, and ours currently hands it wildly tiled coordinates -- a plausible
source of "the texture was not scaled properly, too much area was applied".

This is a surgical byte edit: only the 8 bytes of uv1 and the 8 bytes of uv2 in
each vertex move.  Everything else (uv0, positions, normals, weights, indices,
TOC, GPU size) is verified byte-identical.

usage:  set_uv12.py --src <patch_0> --out <dir> [--unit 0x.. ] [--dry-run]
"""
import argparse
import os
import struct
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_patch as HP
import hd2_patchw as PW
import hd2_unit as U

PATCH_NAME = '9ba626afa44a3aa3.patch_0'
DEFAULT_UNITS = [0xCAE35EE15168145F, 0x9841B16544C8FBAE]
UV12_VALUE = (0.0, 1.0)


def log(m=''):
    print(m, flush=True)


def component_offsets(u, si):
    """-> {index: (offset, size)} keyed by the component's `index` field."""
    out = {}
    off = 0
    for c in u.stream_infos[si].components:
        size = U.component_size(c.format, u.version)
        out[c.index] = (off, size)
        off += size
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--name', default='lunalice-uv12')
    ap.add_argument('--unit', action='append', default=[],
                    help='unit id to patch (repeatable); default both RE-2310 torsos')
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    units = set(int(x, 16) for x in args.unit) if args.unit else set(DEFAULT_UNITS)
    log('source : %s' % args.src)
    log('units  : %s' % ', '.join('0x%016x' % u for u in sorted(units)))

    entries, _ = HP.scan_entries(args.src)
    out_entries = []
    total_changed = 0

    for e in entries:
        pl = HP.entry_payload(e, args.src)
        if e['type_id'] == U.UNIT_ID and e['file_id'] in units:
            before = bytes(pl['gpu'])
            gpu = bytearray(before)
            uu = U.Unit.read(pl['toc'], before)
            log('unit 0x%016x  %d streams' % (e['file_id'], len(uu.stream_infos)))
            for s, si in enumerate(uu.stream_infos):
                offs = component_offsets(uu, s)
                if 1 not in offs or 2 not in offs:
                    log('  stream %d: no uv1/uv2 components; skipped' % s)
                    continue
                stride = si.vertex_stride
                base0 = si.vertex_buffer_offset
                n = si.num_vertices
                n_ch = 0
                for i in range(n):
                    b = base0 + i * stride
                    for ch in (1, 2):
                        o, size = offs[ch]
                        if size != 8:
                            raise AssertionError('uv%d is %d bytes, expected 8'
                                                 % (ch, size))
                        cur = struct.unpack_from('<2f', gpu, b + o)
                        if cur != UV12_VALUE:
                            struct.pack_into('<2f', gpu, b + o, *UV12_VALUE)
                            n_ch += 1
                log('  stream %d: %6d verts, stride %d, uv1@%d uv2@%d -> %d changed'
                    % (s, n, stride, offs[1][0], offs[2][0], n_ch))
                total_changed += n_ch

            after = bytes(gpu)
            assert len(after) == len(before), 'gpu length changed'
            diff = np.frombuffer(before, np.uint8) != np.frombuffer(after, np.uint8)
            nbytes = int(diff.sum())
            log('  byte diff: %d of %d (%.3f%%)' % (nbytes, len(before),
                                                    100.0 * nbytes / len(before)))
            out_entries.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'],
                                        pl['stream'], after,
                                        e['unknown1'], e['unknown2'],
                                        note='unit:%s uv12 const' % args.name))
        else:
            out_entries.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'],
                                        pl['stream'], pl['gpu'],
                                        e['unknown1'], e['unknown2'],
                                        note='verbatim'))

    if total_changed == 0:
        raise AssertionError('nothing changed; refusing to write')

    if args.dry_run:
        log('dry run: nothing written')
        return 0

    res = PW.write_patch(out_entries, args.src, args.out, PATCH_NAME)
    src_gpu = os.path.getsize(args.src + '.gpu_resources')
    log('  wrote %s (%d B) + gpu (%d B; source %d)'
        % (res['toc'], res['toc_size'], res['gpu_size'], src_gpu))
    assert res['gpu_size'] == src_gpu, 'gpu blob size changed'
    log('  vertices with uv1/uv2 rewritten: %d' % total_changed)
    return 0


if __name__ == '__main__':
    sys.exit(main())
