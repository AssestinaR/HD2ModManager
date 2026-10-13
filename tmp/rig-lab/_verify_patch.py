# -*- coding: utf-8 -*-
"""Read the built patch back and check every load-bearing invariant.

This is deliberately independent of `build_patch.py`: it re-reads the files
from disk with the *reader* modules, so a writer/reader pair that agree on a
shared bug still has to survive the reference units and the game's contracts.

Round 59 review asked for four things this file now does:

  1. **The declared `MeshInfo` bounds must describe the mesh's own geometry.**
     Round 59 shipped a stale torso box on a whole-body mesh (66290 of 121704
     vertices outside their own declared bounds).
  2. **The whole chain, not a spot check.**  Every LOD, every palette entry,
     every remap, every section and every material slot participates; a missing
     avatar hash is a failure rather than a `continue`.
  3. **The section table must tile the index buffer** -- equal totals do not
     prove no overlap and no hole.
  4. **Negative controls.**  `--negative` mutates an in-memory copy of the built
     unit seven ways and asserts that the matching check actually fails.  A
     check that cannot fail is not a check.

usage:
    _verify_patch.py [--out DIR] [--name NAME] [--negative]
"""
import argparse
import copy
import json
import os
import struct
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_asset as A
import hd2_patch as HP
import hd2_unit as U
import hd2_unitbuild as UB
import hd2_unitw as UW
import hd2_vertex as V

DEFAULT_OUT = r'E:\mod\Lunalice-RigTest'
PATCH_NAME = '9ba626afa44a3aa3.patch_0'
NPZ = os.path.join(HERE, 'out', 'blender', 'model.npz')
AVATAR_TOC = os.path.join(HERE, 'avatar', 'unit_0x4d1c334d294dfa97.toc')
DONOR_PATCH = r'E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310\9ba626afa44a3aa3.patch_0'
DONOR_UNIT = 0x9841B16544C8FBAE
NATIVE_DIR = r'E:\tmp\hd2-mgroot\orig'
LAMBERT1_FILE_ID = 0x84B327056BAF1C6D


class Report:
    def __init__(self):
        self.rows = []
        self.fail = []

    def check(self, name, ok, detail=''):
        ok = bool(ok)
        self.rows.append((name, ok, detail))
        if not ok:
            self.fail.append(name)
        print('  [%s] %-56s %s' % ('ok' if ok else 'FAIL', name, detail))
        return ok

    def names(self):
        return {n for n, ok, _ in self.rows if not ok}


# --------------------------------------------------------------------- audit
def audit(u, ctx, rep, header=True):
    """Every invariant, on whatever `u` is handed in (real or mutant)."""
    ti = u.transform_info
    d = ctx['npz']
    pos = d['ours_bind']
    tris = np.asarray(d['tris'], np.int64)

    if header:
        print('-' * 78)

    # ---- stream / component contract --------------------------------------
    stride_ok, stride_detail = True, []
    for i, s in enumerate(u.stream_infos):
        want = sum(U.component_size(c.format, u.version) for c in s.components)
        if want != s.vertex_stride:
            stride_ok = False
            stride_detail.append('s%d declares %d, components sum to %d'
                                 % (i, s.vertex_stride, want))
        if s.num_vertices * s.vertex_stride != len(u.gpu_vertex[i]) and \
                s.vertex_buffer_size < s.num_vertices * s.vertex_stride:
            stride_ok = False
            stride_detail.append('s%d vertex payload is short' % i)
    rep.check('every stream stride equals its component sizes', stride_ok,
              '; '.join(stride_detail) or '%d streams' % len(u.stream_infos))

    # ---- MeshInfo bounds must describe the mesh's own geometry -------------
    bounds_bad = []
    for m in u.mesh_infos:
        verts = (ctx['cull_a'] if m.lod_index < 0 and m.stream_index == 3
                 else ctx['cull_b'] if m.lod_index < 0 else pos)
        mn, mx, rad = bounds_of(verts, m.sections)
        st = struct.unpack_from('<7f', m.unk2, 0)
        if not (np.allclose(st[0:3], mn, atol=1e-6)
                and np.allclose(st[3:6], mx, atol=1e-6)
                and abs(st[6] - rad) < 1e-5):
            outside = int(((verts < mn - 1e-6).any(axis=1)
                           | (verts > mx + 1e-6).any(axis=1)).sum())
            bounds_bad.append('lod%d stored %s..%s r=%.4f, draws %s..%s r=%.4f, '
                              '%d verts outside'
                              % (m.lod_index, np.round(st[0:3], 3), np.round(st[3:6], 3),
                                 st[6], np.round(mn, 3), np.round(mx, 3), rad, outside))
    rep.check('MeshInfo bounds describe the mesh\'s own geometry', not bounds_bad,
              '; '.join(bounds_bad) or '%d meshes' % len(u.mesh_infos))

    # ---- section table ----------------------------------------------------
    sec_ok, sec_detail = True, []
    for m in u.mesh_infos:
        s = u.stream_infos[m.stream_index]
        seen = []
        for sec in m.sections:
            if sec.material_index >= len(m.material_ids):
                sec_ok = False
                sec_detail.append('lod%d section material_index %d out of %d slots'
                                  % (m.lod_index, sec.material_index, len(m.material_ids)))
            if sec.vertex_offset + sec.num_vertices > s.num_vertices:
                sec_ok = False
                sec_detail.append('lod%d vertex window %d+%d > %d'
                                  % (m.lod_index, sec.vertex_offset, sec.num_vertices,
                                     s.num_vertices))
            if sec.index_offset + sec.num_indices > s.num_indices:
                sec_ok = False
                sec_detail.append('lod%d index window %d+%d > %d'
                                  % (m.lod_index, sec.index_offset, sec.num_indices,
                                     s.num_indices))
            seen.append((sec.index_offset, sec.index_offset + sec.num_indices))
        seen.sort()
        for (a0, a1), (b0, b1) in zip(seen, seen[1:]):
            if b0 < a1:
                sec_ok = False
                sec_detail.append('lod%d index ranges overlap at %d' % (m.lod_index, b0))
    rep.check('every section window is inside its stream and they do not overlap',
              sec_ok, '; '.join(sec_detail[:3]) or '%d meshes' % len(u.mesh_infos))

    lod0 = next(m for m in u.mesh_infos if m.lod_index == 0)
    tiles = sorted((s.index_offset, s.num_indices) for s in lod0.sections)
    cursor, holes = 0, []
    for off, cnt in tiles:
        if off != cursor:
            holes.append(off)
        cursor = off + cnt
    rep.check('LOD0 sections tile the index buffer with no gap',
              not holes and cursor == u.stream_infos[0].num_indices,
              'gaps at %s, ends at %d of %d' % (holes[:3], cursor,
                                                u.stream_infos[0].num_indices))

    # ---- material chain (section -> mesh slot -> unit slot -> asset) -------
    patch_mats = ctx['patch_materials']
    chain_bad = []
    for m in u.mesh_infos:
        for k, short in enumerate(m.material_ids):
            slots = [i for i, x in enumerate(u.section_ids) if x == short]
            if not slots:
                chain_bad.append('lod%d slot %d short id %x has no unit slot'
                                 % (m.lod_index, k, short))
                continue
            if m.lod_index < 0:
                if any(u.material_ids[s] != LAMBERT1_FILE_ID for s in slots):
                    chain_bad.append('culling slot %d is not the base Lambert1' % k)
            elif not any(u.material_ids[s] in patch_mats for s in slots):
                chain_bad.append('lod%d slot %d resolves to no Material entry'
                                 % (m.lod_index, k))
        for sec in m.sections:
            short = m.material_ids[sec.material_index]
            if not any(x == short for x in u.section_ids):
                chain_bad.append('lod%d section -> short id %x not in the unit table'
                                 % (m.lod_index, short))
    rep.check('section -> mesh slot -> unit slot -> Material asset resolves',
              not chain_bad, '; '.join(chain_bad[:3]) or '%d meshes' % len(u.mesh_infos))

    # ---- TransformInfo ----------------------------------------------------
    av = ctx['avatar']
    missing = [h for h in av.hashes if h not in set(ti.hashes)]
    rep.check('no avatar bone is missing from TransformInfo', not missing,
              '%d missing' % len(missing))
    rep.check('TransformInfo has no duplicate name hash',
              len(set(ti.hashes)) == ti.count,
              '%d distinct of %d' % (len(set(ti.hashes)), ti.count))
    rep.check('every TransformInfo parent index is in range',
              all(0 <= par < ti.count for _, par in ti.entries))
    cyclic = 0
    for i in range(ti.count):
        seen, j = set(), i
        while ti.entries[j][1] != j:
            if j in seen or len(seen) > ti.count:
                cyclic += 1
                break
            seen.add(j)
            j = ti.entries[j][1]
    rep.check('every parent chain terminates at its own root', cyclic == 0,
              '%d cyclic' % cyclic)

    # ---- mesh transform / MeshID -----------------------------------------
    rep.check('MeshID == NameHashes[TransformIndex] for all meshes',
              all(m.mesh_id == ti.hashes[m.transform_index] for m in u.mesh_infos))
    worst_t = 0.0
    for m in u.mesh_infos:
        if m.lod_index < 0:
            continue
        worst_t = max(worst_t, float(np.abs(
            np.asarray(ti.matrices[m.transform_index], np.float64).reshape(4, 4)
            - np.eye(4)).max()))
    rep.check('every LOD mesh transform is the identity', worst_t < 1e-6,
              'worst |M - I| = %.2e' % worst_t)

    # ---- BoneInfo: every LOD, every palette entry -------------------------
    bi_ok, bi_detail = True, []
    if len(u.bone_infos) != 4:
        bi_ok, bi_detail = False, ['%d BoneInfos' % len(u.bone_infos)]
    for i, bi in enumerate(u.bone_infos):
        if len(bi.bones) != len(bi.real_indices) or bi.num_bones != len(bi.bones):
            bi_ok = False
            bi_detail.append('BoneInfo %d arrays disagree' % i)
            continue
        if bi.remap_offsets != UW.remap_offsets_for(bi):
            bi_ok = False
            bi_detail.append('BoneInfo %d remap offsets' % i)
        for k, idx in enumerate(bi.real_indices):
            if not 0 <= idx < ti.count:
                bi_ok = False
                bi_detail.append('BoneInfo %d real index %d out of range' % (i, idx))
                break
            mb = np.asarray(ti.matrices[idx], np.float64).reshape(4, 4)
            bk = np.asarray(bi.bones[k], np.float64).reshape(4, 4)
            if np.abs(mb @ bk - np.eye(4)).max() > 1e-6:
                bi_ok = False
                bi_detail.append('BoneInfo %d bone %d is not inv(M_bone)' % (i, k))
                break
        for r in bi.remaps:
            if len(r) != bi.num_bones or (r and max(r) >= bi.num_bones):
                bi_ok = False
                bi_detail.append('BoneInfo %d has a bad remap' % i)
                break
    rep.check('every BoneInfo inverts its own palette (all LODs, all bones)',
              bi_ok, '; '.join(bi_detail[:3]) or '%d LODs' % len(u.bone_infos))

    # ---- geometry ---------------------------------------------------------
    nv = u.stream_infos[0].num_vertices
    ni = u.stream_infos[0].num_indices
    rep.check('vertex count', nv == len(pos), '%d' % nv)
    rep.check('index count', ni == 3 * len(tris), '%d' % ni)
    rep.check('u32 indices', u.stream_infos[0].index_buffer_type == 1)

    n_real = nv * u.stream_infos[0].vertex_stride
    rep.check('stream 1 mirrors stream 0 (payload)',
              u.gpu_vertex[0][:n_real] == u.gpu_vertex[1][:n_real]
              and u.gpu_index[0] == u.gpu_index[1])
    pad_ok = True
    for i, s in enumerate(u.stream_infos):
        want = (s.vertex_buffer_offset + s.num_vertices * s.vertex_stride + 15) // 16 * 16 \
            - s.vertex_buffer_offset
        if s.vertex_buffer_size != want:
            pad_ok = False
        if u.gpu_vertex[i][s.num_vertices * s.vertex_stride:].strip(b'\x00'):
            pad_ok = False
    rep.check('declared VertexBufferSize == align16(payload), pad is zero', pad_ok)

    vs = u.decode_vertices(0)
    p = np.asarray([v['position'] for v in vs], np.float64)
    rep.check('positions decode back to ours_bind', float(np.abs(p - pos).max()) < 1e-5,
              'max |d| = %.3e m' % float(np.abs(p - pos).max()))
    w = np.asarray([v['bone_weight'] for v in vs], np.float64)
    rep.check('weights close under fsum(half)',
              bool(np.all(np.abs(w.sum(axis=1) - 1.0) < 1e-9)),
              'worst %.3e' % float(np.abs(w.sum(axis=1) - 1.0).max()))
    bi0 = u.bone_infos[0]
    idx = np.asarray([v['bone_index0'][0] for v in vs])
    rep.check('bone indices inside the palette', int(idx.max()) < bi0.num_bones,
              'max %d of %d' % (idx.max(), bi0.num_bones))

    tri = np.asarray(u.index_list(0), np.int64).reshape(-1, 3)
    nrm = np.asarray([_oct(v['normal']) for v in vs])
    g = V.geometric_normals(pos.astype(np.float64), tris)
    ang = np.degrees(np.arccos(np.clip(np.einsum('ij,ij->i', nrm, g), -1, 1)))
    rep.check('stored normals agree with the geometry', np.median(ang) < 20.0,
              'median %.1f deg, p90 %.1f deg' % (np.median(ang), np.percentile(ang, 90)))
    # the encoder itself must round-trip: encode(decode(x)) lands back on x
    back = np.asarray([_oct(int(x)) for x in UB.oct_encode(nrm)])
    err = float(np.degrees(np.arccos(np.clip(
        np.einsum('ij,ij->i', back, nrm), -1, 1))).max())
    # the format is 10+10 bits, so a perfect encoder still lands ~0.5 deg away
    # near the octahedral fold; 1 deg separates "quantisation" from "wrong fold"
    rep.check('the octahedral normal encoding round-trips', err < 1.0,
              'worst %.3f deg (10-bit quantisation floor)' % err)
    # A handful of vertices may legitimately face away from the area-weighted
    # normal (thin hair / cloth cards carry two opposed shells), so this is a
    # *bounded* check that also reports where the offenders live -- the round-59
    # review warned that a passing median hides exactly this tail.
    bad = ang > 150
    where = {}
    if bad.any():
        tri_mat = np.empty(len(tris), np.int64)
        for sec in lod0.sections:
            a, b = sec.index_offset // 3, (sec.index_offset + sec.num_indices) // 3
            tri_mat[a:b] = sec.material_index
        vmat = np.full(nv, -1, np.int64)
        vmat[tri[:, 0]] = tri_mat
        for m in np.unique(vmat[bad]):
            where[int(m)] = int((vmat[bad] == m).sum())
    rep.check('no badly inverted normal', float(bad.mean()) < 0.02,
              '%.3f%% beyond 150 deg%s' % (100.0 * bad.mean(),
                                           (' in slots %s' % where) if where else ''))

    rep.check('indices in range', tri.min() >= 0 and tri.max() < nv,
              'min %d max %d' % (tri.min(), tri.max()))
    rep.check('no degenerate triangles',
              bool((tri[:, 0] != tri[:, 1]).all() and (tri[:, 1] != tri[:, 2]).all()
                   and (tri[:, 0] != tri[:, 2]).all()))
    a, b, c = pos[tri[:, 0]], pos[tri[:, 1]], pos[tri[:, 2]]
    vol = float(np.einsum('ij,ij->i', a, np.cross(b, c)).sum() / 6.0)
    rep.check('signed volume is outward', vol > 0, '%+.4f m3' % vol)

    # ---- culling shells are the donor's own -------------------------------
    if ctx.get('donor_cull') is not None:
        # compare the payload only: `VertexBufferSize` also covers the pad up to
        # the next 16-byte boundary, and that pad depends on where the stream
        # happens to start, so it legitimately differs between two patches.
        same = all(bytes(u.gpu_vertex[i][:u.stream_infos[i].num_vertices
                                        * u.stream_infos[i].vertex_stride])
                   == ctx['donor_cull'][i] for i in (2, 3))
        rep.check('culling shells are byte-identical to the reference unit', same)
    return rep


def bounds_of(verts, sections):
    v = np.asarray(verts, np.float64)
    lo = min(int(s.vertex_offset) for s in sections)
    hi = max(int(s.vertex_offset) + int(s.num_vertices) for s in sections)
    sub = v[lo:hi]
    return sub.min(axis=0), sub.max(axis=0), float(np.linalg.norm(sub, axis=1).max())


def _oct(v):
    r = (v & 0x3FF) * (2.0 / 1023.0) - 1.0
    g = ((v >> 10) & 0x3FF) * (2.0 / 1023.0) - 1.0
    z = 1.0 - abs(r) - abs(g)
    if z < 0.0:
        r, g = (1 - abs(g)) * _sgn(r), (1 - abs(r)) * _sgn(g)
    n = np.array([r, g, z], np.float64)
    return n / max(np.linalg.norm(n), 1e-30)


def _sgn(x):
    return 1.0 if x >= 0 else -1.0


def _reorder(u, pl):
    """`serialize_unit` needs the GPU arrays back on the Unit object."""
    u.gpu_vertex = []
    u.gpu_index = []
    gb = U.Buf(pl['gpu'])
    for si in u.stream_infos:
        gb.p = si.vertex_buffer_offset
        u.gpu_vertex.append(gb.raw(si.vertex_buffer_size))
        gb.p = si.index_buffer_offset
        u.gpu_index.append(gb.raw(si.index_buffer_size))
    return u


# ------------------------------------------------------------------ mutation
def mutations(u, ctx):
    """-> [(name, mutate, must_fail_check_substring)] on in-memory copies only."""
    def m_bind(unit):
        # pick a palette entry whose node matrix is actually far from the
        # identity, otherwise setting its inverse-bind to I is a no-op and the
        # control would "pass" without proving anything
        bi = unit.bone_infos[1]
        for k, idx in enumerate(bi.real_indices):
            m = np.asarray(unit.transform_info.matrices[idx], np.float64).reshape(4, 4)
            if np.abs(m - np.eye(4)).max() > 0.05:
                bi.bones[k] = np.eye(4).ravel().tolist()
                break
        return unit

    def m_palette(unit):
        bi = unit.bone_infos[0]
        dev = [float(np.abs(np.asarray(unit.transform_info.matrices[i], np.float64)
                            .reshape(4, 4) - np.eye(4)).max()) for i in bi.real_indices]
        order = np.argsort(dev)[::-1]
        a, b = int(order[0]), int(order[1])
        bi.real_indices[a], bi.real_indices[b] = bi.real_indices[b], bi.real_indices[a]
        return unit

    def m_avatar(unit):
        av = ctx['avatar']
        drop = av.hashes[7]
        i = unit.transform_info.hashes.index(drop)
        del unit.transform_info.hashes[i]
        del unit.transform_info.locals[i]
        del unit.transform_info.matrices[i]
        del unit.transform_info.entries[i]
        unit.transform_info.count -= 1
        return unit

    def m_sections(unit):
        s = unit.mesh_infos[3].sections
        s[0].index_offset, s[1].index_offset = s[1].index_offset, s[0].index_offset
        return unit

    def m_bbox(unit):
        unit.mesh_infos[0].unk2 = ctx['donor_mesh_unk2']
        return unit

    def m_transform(unit):
        m = unit.transform_info.matrices[unit.mesh_infos[3].transform_index]
        m[12], m[13], m[14] = 0.1, 0.2, 0.3
        return unit

    def m_stride(unit):
        unit.stream_infos[0].vertex_stride = 44
        return unit

    return [
        ('a BoneInfo no longer inverts its palette', m_bind, 'BoneInfo'),
        ('two palette entries swapped', m_palette, 'BoneInfo'),
        ('an avatar hash deleted', m_avatar, 'avatar bone is missing'),
        ('two section index ranges swapped', m_sections, 'do not overlap'),
        ('the donor\'s stale bbox restored', m_bbox, 'own geometry'),
        ('a LOD transform made non-identity', m_transform, 'transform is the identity'),
        ('a stream stride that contradicts its components', m_stride, 'stride equals'),
    ]


# ---------------------------------------------------------------------- main
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default=DEFAULT_OUT)
    ap.add_argument('--name', default='lunalice-rigtest')
    ap.add_argument('--patch', default=PATCH_NAME)
    ap.add_argument('--negative', action='store_true')
    args = ap.parse_args()

    toc_path = os.path.join(args.out, args.patch)
    man = json.load(open(os.path.join(args.out, args.name + '.manifest.json'),
                         encoding='utf-8'))
    entries, raw = HP.scan_entries(toc_path)
    print('patch  : %s (%d bytes, %d entries)' % (toc_path, len(raw), len(entries)))

    by_type = {}
    for e in entries:
        by_type.setdefault(e['type_id'], []).append(e)
    print('  types: ' + ', '.join('0x%x x%d' % (t, len(v)) for t, v in by_type.items()))

    ue = by_type[U.UNIT_ID][0]
    pl = HP.entry_payload(ue, toc_path)
    u = U.Unit.read(pl['toc'], pl['gpu'])

    # the donor's own culling payload, for the "shells unchanged" check
    donor_cull = None
    if os.path.exists(DONOR_PATCH):
        dents, _ = HP.scan_entries(DONOR_PATCH)
        de = next((x for x in dents if x['file_id'] == DONOR_UNIT
                   and x['type_id'] == U.UNIT_ID), None)
        if de is not None:
            du = U.Unit.read(*_parts(HP.entry_payload(de, DONOR_PATCH)))
            donor_cull = {i: bytes(du.gpu_vertex[i])[:du.stream_infos[i].num_vertices
                                                     * du.stream_infos[i].vertex_stride]
                          for i in (2, 3)}

    av_raw = open(AVATAR_TOC, 'rb').read()
    avatar = U.TransformInfo.read(U.Buf(av_raw), struct.unpack_from('<I', av_raw, 0x34)[0])

    cull_a = np.asarray([v['position'] for v in u.decode_vertices(3)], np.float64)
    cull_b = np.asarray([v['position'] for v in u.decode_vertices(2)], np.float64)

    ctx = dict(npz=np.load(NPZ), avatar=avatar,
               patch_materials={e['file_id'] for e in by_type.get(A.MATERIAL_TYPE_ID, [])},
               cull_a=cull_a, cull_b=cull_b, donor_cull=donor_cull,
               donor_mesh_unk2=_donor_unk2())

    rep = Report()
    # ---- container --------------------------------------------------------
    rep.check('entry count matches the manifest', len(entries) == man['entryCount'],
              '%d' % len(entries))
    rep.check('unit count matches the manifest',
              len(by_type.get(U.UNIT_ID, [])) == man['unitCount'],
              '%d unit(s), mode %s' % (len(by_type.get(U.UNIT_ID, [])), man.get('unitMode')))
    rep.check('the unit ids are the ones the manifest lists',
              sorted('%016x' % e['file_id'] for e in by_type.get(U.UNIT_ID, []))
              == sorted(man['unitIds']))
    rep.check('material count', len(by_type.get(A.MATERIAL_TYPE_ID, [])) == len(man['materials']),
              '%d' % len(by_type.get(A.MATERIAL_TYPE_ID, [])))
    rep.check('texture count', len(by_type.get(A.TEXTURE_TYPE_ID, [])) == len(man['textures']),
              '%d' % len(by_type.get(A.TEXTURE_TYPE_ID, [])))
    rep.check('Bone type kept non-empty (template parity)',
              len(by_type.get(U.BONE_ID, [])) == 2)
    rep.check('magic', struct.unpack_from('<I', raw, 0)[0] == HP.MAGIC)
    ntypes = struct.unpack_from('<I', raw, 4)[0]
    declared = sum(struct.unpack_from('<Q', raw, 72 + i * 32 + 16)[0] for i in range(ntypes))
    rep.check('declared file count == entry count', declared == len(entries), '%d' % declared)
    rep.check('first TocDataOffset == header + entries',
              min(e['toc_offset'] for e in entries) == 72 + ntypes * 32 + len(entries) * 80)

    # ---- native carrier ---------------------------------------------------
    if man.get('unitMode') == 'native':
        car = man['nativeUnits'][0]
        path = car['source']
        cu = U.Unit.read(open(path, 'rb').read(), b'')
        rep.check('the unit id is the requested native carrier',
                  '%016x' % ue['file_id'] == car['id'], '0x%016x' % ue['file_id'])
        rep.check('the carrier\'s own customization_data is carried over',
                  u.customization_data == cu.customization_data,
                  '%d B' % len(u.customization_data))
        rep.check('the carrier\'s version is kept', u.version == cu.version,
                  '0x%08x' % u.version)

    print('-' * 78)
    audit(u, ctx, rep)

    # ---- material / texture payloads --------------------------------------
    print('-' * 78)
    mfile = {e['file_id'] for e in by_type.get(A.MATERIAL_TYPE_ID, [])}
    tfile = {e['file_id'] for e in by_type.get(A.TEXTURE_TYPE_ID, [])}
    ok_tex, ntex = True, 0
    for e in by_type.get(A.MATERIAL_TYPE_ID, []):
        m = A.Material.read(HP.entry_payload(e, toc_path)['toc'])
        if len(m.tex_ids) != 3 or m.parent_name != 'basic':
            ok_tex = False
        for tid in m.tex_ids:
            ntex += 1
            if tid not in tfile:
                ok_tex = False
    rep.check('every material is a `basic` with 3 in-patch textures', ok_tex,
              '%d references' % ntex)
    for e in by_type.get(A.TEXTURE_TYPE_ID, []):
        t = A.Texture.read(HP.entry_payload(e, toc_path)['toc'])
        if t.dds_header[84:88] != b'DX10':
            ok_tex = False
    rep.check('every texture uses a DX10 DDS header', ok_tex)

    # ---- protocol ---------------------------------------------------------
    print('-' * 78)
    proto = open(os.path.join(args.out, args.name + '.protocol'), encoding='utf-8').read()
    lines = [l for l in proto.splitlines() if l and not l.startswith('#')]
    pieces = [l for l in lines if l.startswith('model_piece=')]
    unit_hex = {'%016x' % e['file_id'] for e in by_type[U.UNIT_ID]}
    rep.check('protocol declares the model', any(l.startswith('model=') for l in lines))
    rep.check('every protocol piece references a unit in the patch',
              all(l.split(',')[2] in unit_hex for l in pieces), '%d pieces' % len(pieces))
    if man.get('unitMode') == 'native':
        rep.check('a native replacement leaves the runtime mapping off',
                  'enabled=false' in lines,
                  'the game resolves the id itself; nothing may remap it')
    else:
        rep.check('the protocol references every unit in the patch',
                  set(l.split(',')[2] for l in pieces) == unit_hex)

    # ---- negative controls ------------------------------------------------
    if args.negative:
        print('=' * 78)
        print('negative controls: each mutation must make its own check fail')
        for label, mutate, needle in mutations(u, ctx):
            mutant = mutate(copy.deepcopy(u))
            sub = Report()
            audit(mutant, ctx, sub, header=False)
            hits = [n for n in sub.names() if needle in n]
            rep.check('  control: %s' % label, bool(hits),
                      ('caught by %r' % hits[0]) if hits
                      else 'NOT CAUGHT -- the check cannot fail')

    print('=' * 78)
    if rep.fail:
        print('FAILED %d check(s): %s' % (len(rep.fail), ', '.join(rep.fail)))
        return 1
    print('all %d checks passed' % len(rep.rows))
    return 0


def _parts(pl):
    return pl['toc'], pl['gpu']


def _donor_unk2():
    if not os.path.exists(DONOR_PATCH):
        return b'\x00' * 32
    ents, _ = HP.scan_entries(DONOR_PATCH)
    e = next(x for x in ents if x['file_id'] == DONOR_UNIT and x['type_id'] == U.UNIT_ID)
    du = U.Unit.read(*_parts(HP.entry_payload(e, DONOR_PATCH)))
    return du.mesh_infos[0].unk2


if __name__ == '__main__':
    sys.exit(main())
