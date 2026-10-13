"""Decide, by measurement, whether the source VTX index blocks are triangle
LISTS or triangle STRIPS.

Signals (none of which trusts our own normal pipeline):
  A. triangle size   -- a wrong interpretation connects unrelated vertices, so
                        edge lengths / total area blow up
  B. winding         -- a correct reading has ~99% of faces agreeing with the
                        authored vertex normals (the shipping reference units
                        measure 97.8-100%)
  C. degenerates     -- triangle strips carry connector degenerates (i[k]==i[k+2])
"""
import os
import struct
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_source as S
import export_obj as E
import hd2_style as H

MODEL = 'survivor_teenangst'


def read_vtx_groups(vtx):
    bpc, = struct.unpack_from('<i', vtx, 28)
    bpo, = struct.unpack_from('<i', vtx, 32)
    groups = []
    for i in range(bpc):
        e = bpo + i * 8
        mc, = struct.unpack_from('<i', vtx, e)
        mo, = struct.unpack_from('<i', vtx, e + 4)
        mstart = e + mo
        for mi in range(mc):
            me = mstart + mi * 8
            mlc, = struct.unpack_from('<i', vtx, me)
            mlo, = struct.unpack_from('<i', vtx, me + 4)
            lstart = (me + mlo) if mlc else 0
            for li in range(mlc):
                le = lstart + li * 12
                xc, = struct.unpack_from('<i', vtx, le)
                xo, = struct.unpack_from('<i', vtx, le + 4)
                xstart = (le + xo) if xc else 0
                for xi in range(xc):
                    xe = xstart + xi * 9
                    gc, = struct.unpack_from('<i', vtx, xe)
                    go, = struct.unpack_from('<i', vtx, xe + 4)
                    gstart = (xe + go) if gc else 0
                    for gi in range(gc):
                        ge = gstart + gi * 25
                        vc, = struct.unpack_from('<i', vtx, ge)
                        vo, = struct.unpack_from('<i', vtx, ge + 4)
                        ic, = struct.unpack_from('<i', vtx, ge + 8)
                        io, = struct.unpack_from('<i', vtx, ge + 12)
                        fl = vtx[ge + 16]
                        vstart, istart = ge + vo, ge + io
                        orig = [struct.unpack_from('<H', vtx, vstart + v * 9 + 4)[0]
                                for v in range(vc)]
                        idx = [struct.unpack_from('<H', vtx, istart + k * 2)[0]
                               for k in range(ic)]
                        groups.append(dict(bp=i, mesh=xi, gi=gi, flags=fl,
                                           vc=vc, ic=ic, orig=orig, idx=idx))
    return groups


def as_list(idx):
    return np.asarray(idx, np.int64).reshape(-1, 3)


def as_strip(idx):
    idx = np.asarray(idx, np.int64)
    t = np.stack([idx[:-2], idx[1:-1], idx[2:]], axis=1)
    t[1::2] = t[1::2][:, [0, 2, 1]]
    return t


def stats(name, P, T, N):
    T = T[T.max(1) < len(P)]
    v0, v1, v2 = P[T[:, 0]], P[T[:, 1]], P[T[:, 2]]
    e = np.stack([np.linalg.norm(v1 - v0, axis=1),
                  np.linalg.norm(v2 - v1, axis=1),
                  np.linalg.norm(v0 - v2, axis=1)], 1)
    ar = np.linalg.norm(np.cross(v1 - v0, v2 - v0), axis=1) * 0.5
    fn = np.cross(v1 - v0, v2 - v0)
    fn /= np.maximum(np.linalg.norm(fn, axis=1, keepdims=True), 1e-30)
    mn = (N[T[:, 0]] + N[T[:, 1]] + N[T[:, 2]]) / 3
    d = (fn * mn).sum(1)
    print('%-6s tris=%-7d edge p50=%.5f p99=%.4f max=%.3f  area=%8.3f m2  '
          'agree=%5.1f%%' % (name, len(T), np.percentile(e, 50),
                             np.percentile(e, 99), e.max(), ar.sum(),
                             100 * (d > 0).mean()))


def main():
    vpk = S.ADDONS + S.MODEL_VPK
    ver, do, ents = E.parse_vpk_tree(vpk)
    vtx = None
    for nm, a, o, l in ents:
        if nm.lower().endswith('survivor_teenangst.dx90.vtx'):
            vtx = E.read_range(vpk, do, a, o, l)
    groups = read_vtx_groups(vtx)
    print('groups=%d  flags=%s' % (len(groups), sorted({g['flags'] for g in groups})))

    mdl_path, mdl, verts, tris, src, short = H.load_source(vpk, MODEL)
    pos = np.asarray([v['pos'] for v in verts], np.float64) * E.IN2M
    nrm = np.asarray([v['nrm'] for v in verts], np.float64)
    nrm /= np.linalg.norm(nrm, axis=1, keepdims=True)
    print('vvd verts=%d  pipeline tris=%d' % (len(pos), len(tris)))

    # ---- replay build_geometry's allocation, emitting BOTH readings ---------
    parts = E.read_mdl_geometry(mdl)
    vparts = E.read_vtx(vtx)[0]
    LT, ST, base, gk = [], [], 0, 0
    for bp_i, mp in enumerate(parts):
        vp = vparts[bp_i]
        for mi, mm in enumerate(mp['models']):
            vm = vp['models'][mi]
            if mm['vertexCount'] == 0 or not vm['lods']:
                continue
            for xi, msh in enumerate(mm['meshes']):
                for g in vm['lods'][0]['meshes'][xi]['groups']:
                    n = len(g['orig'])
                    LT.append(as_list(g['indices']) + base)
                    ST.append(as_strip(g['indices']) + base)
                    base += n
                    gk += 1
    LT, ST = np.concatenate(LT), np.concatenate(ST)
    print('replayed groups=%d verts=%d  list tris=%d  strip tris=%d'
          % (gk, base, len(LT), len(ST)))
    print('pipeline tris=%d  list matches: %s' % (len(tris), len(LT) == len(tris)))
    stats('LIST', pos, LT, nrm)
    stats('STRIP', pos, ST, nrm)

    # ---- degenerates ------------------------------------------------------
    for g in groups[:6]:
        idx = np.asarray(g['idx'], np.int64)
        print('   bp%d m%d g%d flags=%d vc=%d ic=%d  i[k]==i[k+2]:%d  i[k]==i[k+1]:%d'
              % (g['bp'], g['mesh'], g['gi'], g['flags'], g['vc'], g['ic'],
                 int((idx[:-2] == idx[2:]).sum()), int((idx[:-1] == idx[1:]).sum())))


if __name__ == '__main__':
    main()
