# -*- coding: utf-8 -*-
"""Render a unit straight out of a **shipped patch** -- no npz, no rebuild.

Why this exists: every earlier render in the lab came from `out/*/model.npz`,
i.e. from the pipeline's *input*.  Nothing had ever rasterised the bytes that
actually go into the game.  This probe closes that gap: it reads the patch, pulls
the unit's own vertex/index buffers out of the GPU sidecar, applies each mesh's
transform matrix and draws four orthographic views.

`--no-transform` skips the per-mesh matrix, which is the control: if the two
renders differ, the buffers are mesh-local and the matrix is required.

usage:
    _probe_render_patchunit.py <patch_0> <out.png> --unit 0xcae35ee15168145f
"""
import argparse
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_patch as HP
import hd2_unit as U
from _probe_render_npz import W, H, VIEWS, render


def read_unit(patch, unit_id):
    entries, _ = HP.scan_entries(patch)
    for e in entries:
        if e['type_id'] == U.UNIT_ID and e['file_id'] == unit_id:
            pl = HP.entry_payload(e, patch)
            return U.Unit.read(pl['toc'], pl['gpu'])
    raise SystemExit('unit 0x%016x not in %s' % (unit_id, patch))


def gather(u, lod=0, use_transform=True):
    """-> (positions Nx3 in model space, triangles Mx3)."""
    pos_out, tri_out = [], []
    for mi in u.mesh_infos:
        if mi.lod_index != lod:
            continue
        si = u.stream_infos[mi.stream_index]
        verts = u.decode_vertices(mi.stream_index)
        p = np.array([v['position'] for v in verts], np.float64)
        if use_transform:
            m = np.array(u.transform_info.matrices[mi.transform_index],
                         np.float64).reshape(4, 4)
            p = np.hstack([p, np.ones((len(p), 1))]) @ m
            p = p[:, :3]
        raw = u.gpu_index[mi.stream_index]
        if not raw:
            continue
        width = len(raw) // max(si.num_indices, 1)
        dt = {2: '<u2', 4: '<u4'}.get(width)
        if dt is None:
            raise SystemExit('unsupported index width %d' % width)
        idx = np.frombuffer(raw, dt).astype(np.int64)
        base = len(pos_out) and sum(len(x) for x in pos_out) or 0
        pos_out.append(p)
        for sec in mi.sections:
            if sec.num_indices == 0:
                continue
            tri = idx[sec.index_offset:sec.index_offset + sec.num_indices]
            tri_out.append(tri.reshape(-1, 3) + base)
    if not pos_out:
        raise SystemExit('no visible LOD %d' % lod)
    return np.vstack(pos_out), np.vstack(tri_out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch')
    ap.add_argument('out')
    ap.add_argument('--unit', required=True, help='0x<id> of the unit to draw')
    ap.add_argument('--lod', type=int, default=0)
    ap.add_argument('--no-transform', action='store_true')
    args = ap.parse_args()

    uid = int(args.unit, 16)
    u = read_unit(args.patch, uid)
    pos, tris = gather(u, args.lod, not args.no_transform)

    canvas = Image.new('RGB', (W * 4, H + 34), (16, 17, 20))
    dr = ImageDraw.Draw(canvas)
    for i, v in enumerate(VIEWS):
        canvas.paste(render(pos, tris, v), (W * i, 34))
        dr.text((W * i + 10, 11), v[0], fill=(210, 214, 220))
    dr.text((10, 11),
            '%s  unit 0x%016x  LOD%d  %d verts / %d tris%s'
            % (os.path.basename(os.path.dirname(args.patch)), uid, args.lod,
               len(pos), len(tris), '  (no transform)' if args.no_transform else ''),
            fill=(255, 214, 120))
    canvas.save(args.out)
    print('[written] %s  (%dx%d)  %d verts / %d tris'
          % (args.out, canvas.width, canvas.height, len(pos), len(tris)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
