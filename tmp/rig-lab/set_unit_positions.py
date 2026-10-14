# -*- coding: utf-8 -*-
"""把 patch 里 unit 的**顶点位置**整批替换成 `model.npz` 的 `ours_bind`。

背景（第 77b 轮核实）：`armor-normalize` 输出的 unit，其 LOD0/LOD1-3 两个模型 stream
的顶点缓冲与 `dump_blender.py` 写出的 `model.npz['ours_bind']` **一一对应、逐位相同**
（mesh 变换 = 恒等）。所以"换一版 retarget 结果"只需换位置，不必重跑
build_patch / fix_materials / fix_appearance。

⚠️ 只替换**顶点数 > 1000** 的 stream（= 模型本体）；24 顶点的 culling 代理不动。
⚠️ 法线不重算：`hd2_style.py` 的手臂 re-fit（4e）是**纯平移场**，不改变法线。

usage:
    set_unit_positions.py --src <patch_0> --out <dir> --npz <npz_dir>
                          [--unit 0x... --unit 0x...] [--name NAME] [--dry-run]
"""
import argparse
import os
import struct
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_patch as HP
import hd2_patchw as PW
import hd2_unit as U

PATCH_NAME = '9ba626afa44a3aa3.patch_0'
DEFAULT_UNITS = [0xcae35ee15168145f, 0x9841b16544c8fbae]


def field_offsets(u, si):
    off, out = 0, {}
    for c in u.stream_infos[si].components:
        if c.type == 0 and 'pos' not in out:
            out['pos'] = off
        elif c.type == 1 and 'normal' not in out:
            out['normal'] = off
        off += U.component_size(c.format, u.version)
    return out


def model_streams(u, min_verts=1000):
    return [i for i, s in enumerate(u.stream_infos) if s.num_vertices >= min_verts]


def log(m=''):
    print(m, flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--npz', required=True, help='directory holding model.npz')
    ap.add_argument('--unit', action='append', default=[])
    ap.add_argument('--name', default='mourningveil-re2310')
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    units = [int(x, 16) for x in args.unit] or list(DEFAULT_UNITS)

    z = np.load(os.path.join(args.npz, 'model.npz'))
    pos = np.asarray(z['ours_bind'], np.float64)
    log('npz %s  ours_bind %s' % (args.npz, pos.shape))

    entries, _ = HP.scan_entries(args.src)
    log('src %s  %d entries' % (args.src, len(entries)))

    out_entries, report = [], []
    for e in entries:
        pl = HP.entry_payload(e, args.src)
        if e['type_id'] != U.UNIT_ID or e['file_id'] not in units:
            out_entries.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'],
                                        pl['stream'], pl['gpu'],
                                        e['unknown1'], e['unknown2'], note='verbatim'))
            continue

        gpu = bytearray(pl['gpu'])
        uu = U.Unit.read(pl['toc'], bytes(gpu))
        changed = 0
        for s_i in model_streams(uu):
            si = uu.stream_infos[s_i]
            fo = field_offsets(uu, s_i)
            if 'pos' not in fo:
                log('  unit 0x%016x stream %d: no position component, skipped'
                    % (e['file_id'], s_i))
                continue
            assert si.num_vertices == len(pos), \
                'stream %d has %d vertices, npz has %d' % (s_i, si.num_vertices, len(pos))
            base0 = si.vertex_buffer_offset
            stride = si.vertex_stride
            # read current, so we can report the actual delta
            cur = np.asarray([struct.unpack_from('<3f', gpu, base0 + i * stride + fo['pos'])
                              for i in range(si.num_vertices)], np.float64)
            d = np.linalg.norm(cur - pos, axis=1)
            for i in range(si.num_vertices):
                struct.pack_into('<3f', gpu, base0 + i * stride + fo['pos'],
                                 float(pos[i, 0]), float(pos[i, 1]), float(pos[i, 2]))
            changed += int((d > 0).sum())
            log('  unit 0x%016x stream %d: %d verts, %d moved, max |d| %.2f mm'
                % (e['file_id'], s_i, si.num_vertices, int((d > 0).sum()), d.max() * 1000))
        out_entries.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'], pl['stream'],
                                    bytes(gpu), e['unknown1'], e['unknown2'],
                                    note='unit:%s positions <- %s' % (args.name, args.npz)))
        report.append(dict(unit='%016x' % e['file_id'], moved=changed))

    if args.dry_run:
        log('dry run: nothing written')
        return 0

    res = PW.write_patch(out_entries, args.src, args.out, PATCH_NAME)
    src_gpu = os.path.getsize(args.src + '.gpu_resources')
    log('wrote %s (%d B) + gpu (%d B; source %d)'
        % (res['toc'], res['toc_size'], res['gpu_size'], src_gpu))
    assert res['gpu_size'] == src_gpu, 'gpu blob size changed'
    assert res['toc_size'] == os.path.getsize(args.src), 'TOC size changed'
    return 0


if __name__ == '__main__':
    sys.exit(main())
