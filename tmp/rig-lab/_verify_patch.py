# -*- coding: utf-8 -*-
"""Read the built patch back and check every load-bearing invariant.

This is deliberately independent of `build_patch.py`: it re-reads the files
from disk with the *reader* modules, so a writer/reader pair that agree on a
shared bug still has to survive the reference units and the game's contracts.
"""
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
import hd2_unitw as UW
import hd2_vertex as V

OUT = r'E:\mod\Lunalice-RigTest'
TOC = os.path.join(OUT, '9ba626afa44a3aa3.patch_0')
MAN = os.path.join(OUT, 'lunalice-rigtest.manifest.json')
NPZ = os.path.join(HERE, 'out', 'blender', 'model.npz')

FAIL = []


def check(name, ok, detail=''):
    print('  [%s] %-52s %s' % ('ok' if ok else 'FAIL', name, detail))
    if not ok:
        FAIL.append(name)
    return ok


def main():
    man = json.load(open(MAN, encoding='utf-8'))
    entries, raw = HP.scan_entries(TOC)
    print('patch  : %s (%d bytes, %d entries)' % (TOC, len(raw), len(entries)))

    by_type = {}
    for e in entries:
        by_type.setdefault(e['type_id'], []).append(e)
    print('  types: ' + ', '.join('0x%x x%d' % (t, len(v)) for t, v in by_type.items()))

    check('entry count matches the manifest',
          len(entries) == man['entryCount'], '%d' % len(entries))
    check('unit count matches the manifest',
          len(by_type.get(U.UNIT_ID, [])) == man['unitCount'],
          '%d unit(s), mode %s' % (len(by_type.get(U.UNIT_ID, [])), man.get('unitMode')))
    check('the unit ids are the ones the manifest lists',
          sorted('%016x' % e['file_id'] for e in by_type.get(U.UNIT_ID, []))
          == sorted(man['unitIds']))
    check('material count', len(by_type.get(A.MATERIAL_TYPE_ID, [])) == len(man['materials']),
          '%d' % len(by_type.get(A.MATERIAL_TYPE_ID, [])))
    check('texture count', len(by_type.get(A.TEXTURE_TYPE_ID, [])) == len(man['textures']),
          '%d' % len(by_type.get(A.TEXTURE_TYPE_ID, [])))
    check('Bone type kept non-empty (template parity)',
          len(by_type.get(U.BONE_ID, [])) == 2)

    # ---- header / layout ---------------------------------------------------
    check('magic', struct.unpack_from('<I', raw, 0)[0] == HP.MAGIC)
    check('type table starts at 0x48', struct.unpack_from('<I', raw, 4)[0] == 4,
          'numTypes=%d' % struct.unpack_from('<I', raw, 4)[0])
    declared = sum(struct.unpack_from('<Q', raw, 72 + i * 32 + 16)[0]
                   for i in range(struct.unpack_from('<I', raw, 4)[0]))
    check('declared file count == entry count', declared == len(entries), '%d' % declared)
    first = min(e['toc_offset'] for e in entries)
    ntypes = struct.unpack_from('<I', raw, 4)[0]
    check('first TocDataOffset == header + entries',
          first == 72 + ntypes * 32 + len(entries) * 80, '%d' % first)

    # ---- the units ---------------------------------------------------------
    ue = by_type[U.UNIT_ID][0]
    pl = HP.entry_payload(ue, TOC)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    print('-' * 78)
    print('units: %d' % len(by_type[U.UNIT_ID]))
    for e in by_type[U.UNIT_ID]:
        p = HP.entry_payload(e, TOC)
        v = U.Unit.read(p['toc'], p['gpu'])
        print('  0x%016x  toc=%-7d gpu=%-9d nv=%-7d ni=%-7d cust=%d'
              % (e['file_id'], len(p['toc']), len(p['gpu']),
                 v.stream_infos[0].num_vertices, v.stream_infos[0].num_indices,
                 len(v.customization_data)))

    # every unit must be the *same* geometry, byte for byte, apart from the
    # 240-byte customisation blob that tells the game which position it fills
    same, rt, differ = True, True, 0
    base = None
    for e in by_type[U.UNIT_ID]:
        p = HP.entry_payload(e, TOC)
        v = U.Unit.read(p['toc'], p['gpu'])
        if UW.serialize_unit(_reorder(v, p)) != p['toc']:
            rt = False
        if base is None:
            base = p['toc']
        elif p['toc'] != base:
            differ += 1
            # locate the differing bytes: must be inside the customisation blob
            v0 = U.Unit.read(base, p['gpu'])
            a, b = v0.customization_data, v.customization_data
            if len(a) != len(b) or not a or not b:
                same = False
                continue
            i0 = base.find(a)
            if i0 < 0 or p['toc'][i0:i0 + len(b)] != b:
                same = False
            for j, (x, y) in enumerate(zip(base, p['toc'])):
                if x != y and not (i0 <= j < i0 + len(a)):
                    same = False
                    break
    check('every unit TOC round-trips byte for byte', rt)
    check('units differ only in the 240-byte customisation blob', same,
          '%d of %d differ' % (differ, len(by_type[U.UNIT_ID])))
    check('unit toc round-trips byte for byte',
          UW.serialize_unit(_reorder(u, pl)) == pl['toc'],
          '%d bytes' % len(pl['toc']))

    d = np.load(NPZ)
    pos = d['ours_bind']
    ti = u.transform_info
    check('transform info = donor nodes + the missing avatar bones',
          ti.count == 115, '%d nodes' % ti.count)
    check('TransformInfo has no duplicate name hash',
          len(set(ti.hashes)) == ti.count,
          '%d distinct of %d' % (len(set(ti.hashes)), ti.count))
    check('every TransformInfo parent index is in range',
          all(0 <= par < ti.count for _, par in ti.entries))
    check('4 BoneInfos', len(u.bone_infos) == 4)
    bi = u.bone_infos[0]
    check('palette covers all 107 bones', bi.num_bones == 107, '%d' % bi.num_bones)
    check('one remap per material', bi.num_remaps == len(u.mesh_infos[0].material_ids),
          '%d remaps / %d material slots' % (bi.num_remaps,
                                             len(u.mesh_infos[0].material_ids)))
    check('remap offsets follow the SDK rule',
          bi.remap_offsets == UW.remap_offsets_for(bi), str(bi.remap_offsets))
    check('every remap is the identity',
          all(r == list(range(107)) for r in bi.remaps))

    # every mesh is resolved through its transform node: MeshID == that node's hash
    check('MeshID == NameHashes[TransformIndex] for all six meshes',
          all(m.mesh_id == ti.hashes[m.transform_index] for m in u.mesh_infos))
    # and every visible LOD mesh must sit on an identity node, or the game
    # displaces the mesh by that node's translation (this is what lifted the
    # model ~0.97 m in round 58)
    worst_t = 0.0
    for m in u.mesh_infos:
        if m.lod_index < 0:
            continue
        worst_t = max(worst_t, float(np.abs(
            np.asarray(ti.matrices[m.transform_index], np.float64).reshape(4, 4)
            - np.eye(4)).max()))
    check('every LOD mesh transform is the identity', worst_t < 1e-6,
          'worst |M - I| = %.2e' % worst_t)

    # the merged bones must carry the avatar's locals AND matrices, and the
    # hierarchy must be closed (parent chain walks to the root)
    raw = open(os.path.join(HERE, 'avatar', 'unit_0x4d1c334d294dfa97.toc'), 'rb').read()
    av = U.TransformInfo.read(U.Buf(raw), struct.unpack_from('<I', raw, 0x34)[0])
    av_by_hash = {h: i for i, h in enumerate(av.hashes)}
    by_hash = {h: i for i, h in enumerate(ti.hashes)}
    loc_bad = mat_bad = par_bad = 0
    for k in range(av.count):
        ai = k
        ui = by_hash.get(av.hashes[ai])
        if ui is None:
            continue
        if max(abs(a - b) for a, b in zip(
                ti.locals[ui]['rot'] + ti.locals[ui]['pos'] + ti.locals[ui]['scale'],
                av.locals[ai]['rot'] + av.locals[ai]['pos'] + av.locals[ai]['scale'])) > 1e-9:
            loc_bad += 1
        if not np.allclose(np.asarray(ti.matrices[ui], np.float64),
                           np.asarray(av.matrices[ai], np.float64), atol=1e-6):
            mat_bad += 1
        av_par = av.entries[ai][1]
        want = ui if av_par == ai else by_hash.get(av.hashes[av_par])
        if ti.entries[ui][1] != want:
            par_bad += 1
    check('every avatar bone carries the avatar locals', loc_bad == 0, '%d differ' % loc_bad)
    check('every avatar bone carries the avatar matrix', mat_bad == 0, '%d differ' % mat_bad)
    check('every avatar bone parent is remapped into our index space',
          par_bad == 0, '%d differ' % par_bad)
    depth_bad = 0
    for i in range(ti.count):
        seen, j = set(), i
        while ti.entries[j][1] != j:
            if j in seen:
                depth_bad += 1
                break
            seen.add(j)
            j = ti.entries[j][1]
            if len(seen) > ti.count:
                depth_bad += 1
                break
    check('every parent chain terminates at its own root', depth_bad == 0,
          '%d cyclic' % depth_bad)

    # bones[k] must be inv(M_bone): skinning at rest is the identity
    worst = 0.0
    for k in range(0, 107, 7):
        mb = np.asarray(ti.matrices[bi.real_indices[k]], np.float64).reshape(4, 4)
        bk = np.asarray(bi.bones[k], np.float64).reshape(4, 4)
        worst = max(worst, float(np.abs(mb @ bk - np.eye(4)).max()))
    check('bones[k] == inv(M_bone_k) (rest pose is the identity)', worst < 1e-6,
          'worst |M*inv(M) - I| = %.2e' % worst)

    # ---- geometry ----------------------------------------------------------
    nv = u.stream_infos[0].num_vertices
    ni = u.stream_infos[0].num_indices
    print('-' * 78)
    print('streams: ' + ' | '.join(
        's%d nv=%d stride=%d ni=%d ib=%d' % (i, s.num_vertices, s.vertex_stride,
                                             s.num_indices, s.index_buffer_type)
        for i, s in enumerate(u.stream_infos)))
    check('vertex count', nv == len(pos), '%d' % nv)
    check('index count', ni == 3 * len(d['tris']), '%d' % ni)
    check('u32 indices', u.stream_infos[0].index_buffer_type == 1)
    # `VertexBufferSize` *includes* the pad up to the next absolute 16-byte
    # boundary, so a stream that starts at a non-aligned offset declares a
    # bigger size than its payload.  The reference unit does exactly this too
    # (its culling stream 3 starts at %16 == 8 and declares 8 bytes of pad), so
    # compare payloads and check the pad separately.
    n_real = nv * u.stream_infos[0].vertex_stride
    check('stream 1 mirrors stream 0 (payload)',
          u.gpu_vertex[0][:n_real] == u.gpu_vertex[1][:n_real]
          and u.gpu_index[0] == u.gpu_index[1])
    pad_ok = True
    for i, s in enumerate(u.stream_infos):
        want = (s.vertex_buffer_offset + s.num_vertices * s.vertex_stride + 15) // 16 * 16 \
            - s.vertex_buffer_offset
        if s.vertex_buffer_size != want:
            pad_ok = False
        tail = u.gpu_vertex[i][s.num_vertices * s.vertex_stride:]
        if tail.strip(b'\x00'):
            pad_ok = False
    check('declared VertexBufferSize == align16(payload), pad is zero', pad_ok)

    vs = u.decode_vertices(0)
    p = np.asarray([v['position'] for v in vs], np.float64)
    check('positions decode back to ours_bind',
          float(np.abs(p - pos).max()) < 1e-5,
          'max |d| = %.3e m' % float(np.abs(p - pos).max()))
    w = np.asarray([v['bone_weight'] for v in vs], np.float64)
    check('weights close under fsum(half)',
          bool(np.all(np.abs(w.sum(axis=1) - 1.0) < 1e-9)),
          'worst %.3e' % float(np.abs(w.sum(axis=1) - 1.0).max()))
    idx = np.asarray([v['bone_index0'][0] for v in vs])
    check('bone indices inside the palette', int(idx.max()) < 107, 'max %d' % idx.max())

    # normals: decode the packed oct and compare with the geometric normals
    nrm = np.asarray([_oct(v['normal']) for v in vs])
    g = V.geometric_normals(pos.astype(np.float64),
                            np.asarray(d['tris'], np.int64))
    ang = np.degrees(np.arccos(np.clip(np.einsum('ij,ij->i', nrm, g), -1, 1)))
    check('stored normals agree with the geometry', np.median(ang) < 20.0,
          'median %.1f deg, p90 %.1f deg' % (np.median(ang), np.percentile(ang, 90)))

    tri = np.asarray(u.index_list(0), np.int64).reshape(-1, 3)
    check('indices in range', tri.min() >= 0 and tri.max() < nv,
          'min %d max %d' % (tri.min(), tri.max()))
    check('no degenerate triangles',
          bool((tri[:, 0] != tri[:, 1]).all() and (tri[:, 1] != tri[:, 2]).all()
               and (tri[:, 0] != tri[:, 2]).all()))
    a, b, c = pos[tri[:, 0]], pos[tri[:, 1]], pos[tri[:, 2]]
    vol = float(np.einsum('ij,ij->i', a, np.cross(b, c)).sum() / 6.0)
    check('signed volume is outward', vol > 0, '%+.4f m3' % vol)

    # ---- meshes / sections -------------------------------------------------
    secs = u.mesh_infos[3].sections
    check('LOD0 mesh has one section per material',
          len(secs) == len(u.mesh_infos[3].material_ids), '%d' % len(secs))
    total = sum(s.num_indices for s in secs)
    check('sections cover every index', total == ni, '%d / %d' % (total, ni))
    check('LOD meshes 0..3 all exist', [m.lod_index for m in u.mesh_infos] == [3, 2, 1, 0, -1, -1],
          str([m.lod_index for m in u.mesh_infos]))
    check('every LOD mesh points at our data',
          all(m.stream_index in (0, 1) for m in u.mesh_infos[:4]))
    check('culling meshes have no skinning components',
          all(len(s.components) == 6 for s in u.stream_infos[2:]))

    # ---- material chain ----------------------------------------------------
    print('-' * 78)
    mfile = {e['file_id'] for e in by_type.get(A.MATERIAL_TYPE_ID, [])}
    tfile = {e['file_id'] for e in by_type.get(A.TEXTURE_TYPE_ID, [])}
    ok_chain, ok_tex, ntex = True, True, 0
    for k, (sid, mid) in enumerate(zip(u.section_ids, u.material_ids)):
        if k >= 4 * len(secs):
            continue
        if sid not in u.mesh_infos[3].material_ids and mid not in mfile:
            ok_chain = False
    for k, mid in enumerate(u.material_ids[:4 * len(secs)]):
        if mid not in mfile:
            ok_chain = False
    check('every mesh material slot resolves to a Material entry', ok_chain,
          '%d slots' % (4 * len(secs)))

    tex_seen = 0
    for e in by_type.get(A.MATERIAL_TYPE_ID, []):
        m = A.Material.read(HP.entry_payload(e, TOC)['toc'])
        if len(m.tex_ids) != 3:
            ok_tex = False
        for tid in m.tex_ids:
            ntex += 1
            if tid not in tfile:
                ok_tex = False
        if m.parent_name != 'basic':
            ok_tex = False
    check('every material is a `basic` with 3 in-patch textures', ok_tex,
          '%d references' % ntex)
    for e in by_type.get(A.TEXTURE_TYPE_ID, []):
        t = A.Texture.read(HP.entry_payload(e, TOC)['toc'])
        if t.dds_header[84:88] != b'DX10':
            ok_tex = False
    check('every texture uses a DX10 DDS header', ok_tex)

    # ---- protocol ----------------------------------------------------------
    print('-' * 78)
    proto = open(os.path.join(OUT, 'lunalice-rigtest.protocol'), encoding='utf-8').read()
    lines = [l for l in proto.splitlines() if l and not l.startswith('#')]
    uid = '%016x' % ue['file_id']
    pieces = [l for l in lines if l.startswith('model_piece=')]
    unit_hex = {'%016x' % e['file_id'] for e in by_type[U.UNIT_ID]}
    check('protocol declares the model', any(l.startswith('model=') for l in lines))
    check('every protocol piece references a unit in the patch',
          all(l.split(',')[2] in unit_hex for l in pieces), '%d pieces' % len(pieces))
    check('the protocol references every unit in the patch',
          set(l.split(',')[2] for l in pieces) == unit_hex,
          '%d distinct' % len({l.split(',')[2] for l in pieces}))
    check('protocol parses as 6/7/8 fields per piece',
          all(len(l.split('=')[1].split(',')) in (6, 7, 8) for l in pieces))

    print('=' * 78)
    if FAIL:
        print('FAILED %d check(s): %s' % (len(FAIL), ', '.join(FAIL)))
        return 1
    print('all checks passed')
    return 0


def _oct(v):
    """decode_packed_oct_norm, vector-free."""
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


if __name__ == '__main__':
    sys.exit(main())
