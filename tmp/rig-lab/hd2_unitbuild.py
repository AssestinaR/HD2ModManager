# -*- coding: utf-8 -*-
"""Build a complete HD2 `Unit` asset from scratch for the rig-lab pipeline.

The byte-level writer lives in `hd2_unitw.py` and is proven against all 23
reference units (read -> write -> identical).  What this module adds is the
*semantic* layer: what to put in each field so the game renders our mesh with
our skinning.

Everything that is still unknown-but-load-bearing is copied from a shipping
reference unit rather than invented, and each such copy is labelled below.

Layout we emit (deliberately mirroring the reference torso unit
`0x9841b16544c8fbae` so the LOD group list stays valid):

    TransformInfo = the donor's 23 nodes, with the avatar rig **merged in**
                    (`merge_transform_info`): the 15 hashes that already exist
                    are overwritten with the avatar's locals/matrices/parent,
                    the 92 missing bones are appended.  Never a duplicate hash.
    MeshInfo      = MeshID == NameHashes[TransformIndex], and TransformIndex is
                    the donor's own per-LOD node (5/6/7/8, all identity), so the
                    mesh sits at the origin and `origin == I` in the
                    inverse-joint product.
    BoneInfo      = 4 identical entries (one per LOD), palette = all 107 avatar
                    bones, real_indices[k] = the merged index of bone k,
                    remaps = identity per material, bones[k] = inv(M_bone)
    Streams       = 0: our mesh        1: our mesh again (LOD 1..3)
                    2: culling box B    3: culling box A
    Meshes        = m0..m3 (lod 3..0) -> stream 0/1, sections grouped by material
                    m4/m5 (lod -1)    -> the two culling boxes

Why the mesh appears twice (streams 0 and 1): the reference puts LOD0 in its
own stream and LOD1..3 in a shared one.  We have a single LOD, so both streams
carry the same bytes; the patch-level GPU dedupe then stores it once.
"""
import math
import struct

import numpy as np

import hd2_unit as U
import hd2_unitw as UW

# ---------------------------------------------------------------- primitives
LAMBERT1_FILE_ID = 0x84B327056BAF1C6D      # base-game `Lambert1`
LAMBERT1_SHORT_ID = 0x93FC934              # its section short id


def oct_encode(nrm):
    """(n,3) unit vectors -> (n,) uint32 packed 10-10 octahedral.

    Ported from the SDK (`octahedral_encode` + `encode_packed_oct_norm`).  The
    SDK's own *writer* just passes the uint32 through, so we must do this
    ourselves; `_probe_refwinding.py` confirms the reference units store the
    outward normal this way.
    """
    n = np.asarray(nrm, np.float64)
    l1 = np.abs(n).sum(axis=1, keepdims=True)
    l1 = np.where(l1 == 0.0, 1.0, l1)
    p = n / l1
    neg = p[:, 2] < 0.0
    # The SDK's octahedral fold uses `sign()`, and `sign(0) == +1` there.  NumPy's
    # `np.sign(0)` is `0`, which collapses the fold for a component that is
    # exactly zero: `(0, 0, -1)` would come back as `(0, 0, +1)`.  Measured on our
    # own mesh, the naive version missed the round-trip by up to 89 degrees.
    sx = np.where(p[:, 0] >= 0.0, 1.0, -1.0)
    sy = np.where(p[:, 1] >= 0.0, 1.0, -1.0)
    x = np.where(neg, (1.0 - np.abs(p[:, 1])) * sx, p[:, 0])
    y = np.where(neg, (1.0 - np.abs(p[:, 0])) * sy, p[:, 1])
    r = ((x + 1.0) * (1023.0 / 2.0)).astype(np.uint32)
    g = ((y + 1.0) * (1023.0 / 2.0)).astype(np.uint32)
    return (r | (g << 10)).astype(np.uint32)


def half4_close(weights):
    """-> (ok, worst |fsum(half(w)) - 1|).

    The SDK's `normalize_half4` contract: the float16 values must sum to
    exactly 1 under `math.fsum`.  `dump_blender` already snapped every weight to
    k/2048 (exact in binary16), so this must hold; we assert it rather than
    assume it.
    """
    w = np.asarray(weights, np.float16)
    f = w.astype(np.float64)
    tot = f.sum(axis=1)
    return bool(np.all(tot == 1.0)), float(np.abs(tot - 1.0).max())


# ------------------------------------------------------------- vertex buffer
# component list shared by every stream we emit (same as the reference torso)
MESH_COMPONENTS = [(5, 4, 0),      # color  rgba8
                   (0, 2, 0),      # position vec3 float
                   (1, 30, 0),     # normal unk_normal (oct 10-10)
                   (4, 1, 0),      # uv0 vec2 float
                   (4, 1, 1),      # uv1
                   (4, 1, 2),      # uv2
                   (7, 35, 0),     # bone_weight vec4 half
                   (6, 28, 0)]     # bone_index  vec4 uint8
CULL_COMPONENTS = MESH_COMPONENTS[:6]
MESH_STRIDE = 56
CULL_STRIDE = 44


def pack_mesh_vertices(pos, nrm, uv, bone, weight):
    """-> bytes for one stream: stride 56, components as in MESH_COMPONENTS."""
    nv = len(pos)
    assert pos.shape == (nv, 3) and nrm.shape == (nv, 3) and uv.shape == (nv, 2)
    assert bone.shape == (nv, 4) and weight.shape == (nv, 4)
    col = np.full((nv, 4), 255, np.uint8)
    nor = oct_encode(nrm).view(np.uint8).reshape(nv, 4)
    buf = np.empty((nv, MESH_STRIDE), np.uint8)
    off = 0
    for blob, n in ((col, 4), (pos.astype('<f4').view(np.uint8).reshape(nv, 12), 12),
                    (nor, 4),
                    (uv.astype('<f4').view(np.uint8).reshape(nv, 8), 8),
                    (uv.astype('<f4').view(np.uint8).reshape(nv, 8), 8),
                    (uv.astype('<f4').view(np.uint8).reshape(nv, 8), 8),
                    (weight.astype('<f2').view(np.uint8).reshape(nv, 8), 8),
                    (bone.astype(np.uint8), 4)):
        buf[:, off:off + n] = blob
        off += n
    assert off == MESH_STRIDE
    return buf.tobytes()


def pack_cull_vertices(verts, raw_normals, uv):
    """-> bytes for a culling stream (stride 44, no skinning)."""
    nv = len(verts)
    col = np.zeros((nv, 4), np.uint8)
    nor = np.asarray(raw_normals, np.uint32).view(np.uint8).reshape(nv, 4)
    u = np.asarray(uv, np.float32)
    buf = np.empty((nv, CULL_STRIDE), np.uint8)
    off = 0
    for blob, n in ((col, 4),
                    (verts.astype('<f4').view(np.uint8).reshape(nv, 12), 12),
                    (nor, 4),
                    (u.astype('<f4').view(np.uint8).reshape(nv, 8), 8),
                    (u.astype('<f4').view(np.uint8).reshape(nv, 8), 8),
                    (np.zeros((nv, 2), np.float32).view(np.uint8).reshape(nv, 8), 8)):
        buf[:, off:off + n] = blob
        off += n
    assert off == CULL_STRIDE
    return buf.tobytes()


# ------------------------------------------------------------- mesh bounds
def mesh_bounds(verts, sections):
    """-> (min3, max3, radius) for the vertices this mesh's sections draw.

    This is the rule the shipping units obey, measured three ways in
    `_probe_bounds_rule.py` / `_probe_unk2tail.py`:

    * the three leading pairs are the bbox of the mesh's **own section vertex
      ranges**, not of the whole stream.  The donor's four LOD meshes each store
      a different box even though they share one stream, and each box matches
      exactly its own `vertex_offset .. +num_vertices` window.
    * the seventh float is `max(|v|)` over the same set.  The sphere is centred
      on the origin of the mesh's local space, not on the box centre: the donor
      stores 1.719567 for a mesh whose `max|v|` is 1.717979, while a
      box-centred radius would have been 0.51.
    * everything is in the mesh's **local** space -- before its
      `MeshInfo.TransformIndex` matrix is applied.  Both culling meshes carry a
      bone world matrix as their transform yet store their raw vertex bbox
      verbatim (deviation 0.000000), and the DS-10 carrier's visible mesh stores
      a 0.27x0.16x0.35 box although the same geometry sits around z=1.3 in
      character space.

    Round-59 note: our previous build copied the donor's 32-byte block wholesale
    (`hd2_unitbuild.py:402/419`), which left a torso-sized box on a whole-body
    mesh -- 66290 of 121704 vertices outside their own declared bounds.
    """
    v = np.asarray(verts, np.float64)
    if not sections:
        return v.min(axis=0), v.max(axis=0), float(np.linalg.norm(v, axis=1).max())
    lo_idx = min(int(s.vertex_offset) for s in sections)
    hi_idx = max(int(s.vertex_offset) + int(s.num_vertices) for s in sections)
    assert 0 <= lo_idx < hi_idx <= len(v), \
        'sections reference vertices [%d,%d) but the stream holds %d' % (lo_idx, hi_idx, len(v))
    sub = v[lo_idx:hi_idx]
    return sub.min(axis=0), sub.max(axis=0), float(np.linalg.norm(sub, axis=1).max())


def pack_mesh_unk2(bounds, tail):
    """32-byte `MeshInfo.unk2` = 7 floats (min3, max3, radius) + 4 unknown bytes.

    The trailing four bytes are still unidentified, so they are carried over
    from the donor instead of guessed.  They are per-mesh constants: the donor
    and the vanilla DS-10 carrier independently store `02 00 00 00` for LOD3 and
    `09 00 00 00` for LOD2/1/0, and zero for every culling mesh.
    """
    mn, mx, radius = bounds
    assert len(tail) == 4, 'the unknown 4-byte tail must be preserved verbatim'
    return struct.pack('<7f', float(mn[0]), float(mn[1]), float(mn[2]),
                       float(mx[0]), float(mx[1]), float(mx[2]), float(radius)) + bytes(tail)


# ---------------------------------------------------------------- unit build
def merge_transform_info(ref_ti, avatar, required_hashes):
    """Merge the avatar rig into the donor's TransformInfo -- the SDK algorithm.

    Authoritative sources
    ---------------------
    * `stingray/unit.py` `AddBoneData` (lines 1796-1934).  For every armature
      bone the SDK looks the bone's hash up in the unit's TransformInfo and
      **reuses** the entry when it is already there, otherwise appends one.  A
      new entry gets `Incriment = 1`, and then

          matrices[i] = bone.matrix                  (world, Blender -> Stingray)
          locals[i]   = decompose(parent.matrix^-1 @ bone.matrix)   (parent frame!)
          entries[i]  = (1, index_of(parent_bone_hash))

      i.e. `locals` is the bone's transform **relative to its parent**, not a
      world position.  (Root bones get an all-zero `StingrayLocalTransform`.)
    * `HD2ModAdaptation/.../Canonical/CanonicalTransformInfoExpander.cs`
      `ExpandTransformInfo`.  Same walk, plus one extra rule that we must obey:
      "A same-named target bone is therefore not authoritative: replace its
      transforms and parent link with the Avatar definition too."  And
      `ValidateTransformInfo` requires `NameHashes.Distinct().Count() == count`,
      so a hash must never appear twice.

    Our donor's 23 nodes already carry the four mesh-transform nodes (5..8) and
    the two culling nodes (17/22) that the LOD group list points at.  Those eight
    hashes are *not* in the avatar, so they survive untouched.  The fifteen that
    are shared have locals and matrices byte-identical to the avatar's (measured
    by `_probe_unitdiag.py`), so overwriting them only re-points their parent
    links into our index space -- it cannot move the mesh nodes.

    Returns (TransformInfo, {avatar_index: unit_index}).
    """
    ti = U.TransformInfo()
    ti.count = ref_ti.count
    ti.unk = ref_ti.unk
    ti.locals = [dict(lo) for lo in ref_ti.locals]
    ti.matrices = [list(m) for m in ref_ti.matrices]
    ti.entries = list(ref_ti.entries)
    ti.hashes = list(ref_ti.hashes)

    by_hash = {h: i for i, h in enumerate(ti.hashes)}
    donor_hashes = set(ti.hashes)      # what the donor contributed, for the report
    av_by_hash = {h: i for i, h in enumerate(avatar.hashes)}
    mapping = {}                       # avatar index -> unit index
    replaced = set()                   # donor hashes whose entry we rewrote
    appended = 0

    def ensure(hash_, visiting):
        nonlocal appended
        ai = av_by_hash[hash_]
        av = avatar.entries[ai]
        # a bone is its own parent only when it is the rig root
        parent = None if av[1] == ai else ensure(avatar.hashes[av[1]], visiting)

        existing = by_hash.get(hash_)
        if existing is not None:
            # not authoritative -- take the avatar's locals/matrices/parent
            ti.locals[existing] = dict(avatar.locals[ai])
            ti.matrices[existing] = list(avatar.matrices[ai])
            ti.entries[existing] = (av[0], existing if parent is None else parent)
            if hash_ in donor_hashes:
                replaced.add(hash_)
            mapping[ai] = existing
            return existing

        index = len(ti.hashes)
        ti.hashes.append(hash_)
        ti.locals.append(dict(avatar.locals[ai]))
        ti.matrices.append(list(avatar.matrices[ai]))
        ti.entries.append((av[0], index if parent is None else parent))
        ti.count += 1
        by_hash[hash_] = index
        mapping[ai] = index
        appended += 1
        return index

    for h in required_hashes:
        ensure(h, set())

    # `locals`/`matrices` of a node we kept from the donor must still be the
    # donor's; count them so the caller can assert nothing else moved.
    stats = dict(appended=appended, overwritten=len(replaced),
                 kept=ref_ti.count - len(replaced))
    return ti, mapping, stats


def check_transform_info(ti, expect):
    """Fail loudly on anything the engine (or the SDK validator) would reject."""
    assert ti.count == len(ti.hashes) == len(ti.locals) == len(ti.matrices) \
        == len(ti.entries), 'TransformInfo arrays disagree on length'
    dupes = len(ti.hashes) - len(set(ti.hashes))
    assert dupes == 0, '%d duplicate name hashes in TransformInfo' % dupes
    for i, (inc, par) in enumerate(ti.entries):
        assert 0 <= par < ti.count, 'node %d has parent %d outside the table' % (i, par)
    for i, lo in enumerate(ti.locals):
        assert len(lo['rot']) == 9 and len(lo['pos']) == 3 and len(lo['scale']) == 3, \
            'node %d has a malformed local transform' % i
    for i, m in enumerate(ti.matrices):
        assert len(m) == 16 and all(math.isfinite(v) for v in m), \
            'node %d has a malformed matrix' % i
    if expect:
        for h in expect:
            assert h in ti.hashes, 'TransformInfo is missing required hash 0x%x' % h
    return True


def build_bone_info(ti, transform_indices, material_count):
    """`bones[k] = inverse(TransformInfo.matrices[transform_indices[k]])`.

    The SDK writes `Bones[i] = (origin @ bone.ToBlender()).inverted().transposed()`
    with `origin = mesh_transform.ToBlender().inverted()`
    (`stingray/unit.py` line 2100).  With an identity mesh transform
    `origin == I`, and `transpose(inverse(transpose(M))) == inverse(M)`, so the
    stored matrix is simply the inverse of the bone's world matrix in the same
    (row-major) layout.  Every mesh-transform node we use (the donor's 5..8) is
    the identity, which is why the donor's own 11-bone palette can be reproduced
    this way too.
    """
    bi = U.BoneInfo()
    bi.num_bones = len(transform_indices)
    bi.bones = []
    for idx in transform_indices:
        mb = np.asarray(ti.matrices[idx], np.float64).reshape(4, 4)
        bi.bones.append(np.linalg.inv(mb).ravel().tolist())
    bi.real_indices = [int(x) for x in transform_indices]
    bi.num_remaps = material_count
    bi.remap_counts = [bi.num_bones] * material_count
    bi.remaps = [list(range(bi.num_bones)) for _ in range(material_count)]
    bi.remap_offsets = UW.remap_offsets_for(bi)
    return bi


def _clone_stream(src, num_vertices, num_indices, index_type):
    """Copy a reference StreamInfo's opaque parts and set the sizes we own."""
    si = U.StreamInfo()
    si.component_info_id = src.component_info_id
    si.components = [U.StreamComponentInfo(c.type, c.format, c.index, c.unknown)
                     for c in src.components]
    si.num_components = src.num_components
    si.comp_area_raw = src.comp_area_raw
    si.vertex_buffer_id = src.vertex_buffer_id
    si.vertex_buffer_unk1 = src.vertex_buffer_unk1
    si.vertex_buffer_unk2 = src.vertex_buffer_unk2
    si.vertex_buffer_unk3 = src.vertex_buffer_unk3
    si.index_buffer_id = src.index_buffer_id
    si.index_buffer_unk1 = src.index_buffer_unk1
    si.index_buffer_unk2 = src.index_buffer_unk2
    si.index_buffer_unk3 = src.index_buffer_unk3
    si.unk_ending = src.unk_ending
    si.vertex_stride = src.vertex_stride
    si.num_vertices = num_vertices
    si.num_indices = num_indices
    si.index_buffer_type = index_type
    si.vertex_buffer_offset = si.vertex_buffer_size = 0
    si.index_buffer_offset = si.index_buffer_size = 0
    return si


def build_unit(mesh, groups, ref, avatar, mesh_ids=None, verbose=True):
    """`mesh` is a dict of numpy arrays; `groups` describes the material slots.

    mesh  : pos, nrm, uv, bone, weight, indices (uint32), sections
    groups: list of (material_file_id, section_short_id)
    ref   : reference `hd2_unit.Unit` (structure donor)
    avatar: avatar rig `hd2_unit.TransformInfo`
    """
    nv = len(mesh['pos'])
    ni = len(mesh['indices'])
    assert mesh['indices'].dtype == np.uint32, 'vertex count forces u32 indices'

    u = U.Unit()
    # ---- scalars / opaque blobs copied from the reference torso ------------
    u.unk_ref1 = ref.unk_ref1
    u.bones_ref = ref.bones_ref
    u.unk_ref2 = ref.unk_ref2
    u.state_machine_ref = ref.state_machine_ref
    u.header_data1 = ref.header_data1
    u.version = ref.version
    u.header_data2 = ref.header_data2
    u.header_unk = ref.header_unk
    u.lod_group_list_data = ref.lod_group_list_data
    u.pre_light_list_data = ref.pre_light_list_data
    u.customization_data = ref.customization_data
    u.unk_header_data1 = ref.unk_header_data1
    u.connecting_bone_data = ref.connecting_bone_data
    u.unreversed_data2 = ref.unreversed_data2
    u.light_count = 0
    u.light_unk0 = [0, 0, 0]

    # ---- TransformInfo: the donor's node table + the avatar rig merged in ---
    # `required` is every avatar bone: the SDK's exporter gives every armature
    # bone an entry, and a superset palette keeps the vertex fake indices trivial.
    required = list(avatar.hashes)
    u.transform_info, mapping, stats = merge_transform_info(ref.transform_info,
                                                            avatar, required)
    check_transform_info(u.transform_info, expect=set(required))
    num_bones = len(required)
    if verbose:
        print('  transform info: donor %d nodes + avatar %d bones -> %d nodes '
              '(appended %d, overwritten %d, donor-only kept %d)'
              % (ref.transform_info.count, num_bones, u.transform_info.count,
                 stats['appended'], stats['overwritten'], stats['kept']))

    # ---- mesh transforms: reuse the donor's own per-LOD nodes ---------------
    # `MeshInfo.TransformIndex` must point at a node whose matrix is the mesh's
    # own placement (SDK line 2100: `origin = matrices[TransformIndex]^-1`), and
    # `MeshInfo.MeshID` is the hash of exactly that node.  The donor uses nodes
    # 5/6/7/8 for lod 3/2/1/0 -- all identity -- so reusing them keeps the mesh
    # at the origin instead of stacking it on top of a bone.
    lod_tf = {mi.lod_index: mi.transform_index for mi in ref.mesh_infos
              if mi.lod_index >= 0}
    cull_tf = {mi.stream_index: mi.transform_index for mi in ref.mesh_infos
               if mi.lod_index < 0}
    for lod in (0, 1, 2, 3):
        assert lod in lod_tf, 'the donor has no LOD%d mesh to borrow a transform from' % lod
    for lod, tf_idx in lod_tf.items():
        m = np.asarray(u.transform_info.matrices[tf_idx], np.float64).reshape(4, 4)
        assert np.allclose(m, np.eye(4), atol=1e-6), \
            ('LOD%d mesh transform node %d is not identity (T=%s); bones[] would '
             'need the full inverse-joint product' % (lod, tf_idx, m[3, :3]))
    if verbose:
        print('  mesh transforms: %s  (culling %s)'
              % ({lod: lod_tf[lod] for lod in (0, 1, 2, 3)}, cull_tf))

    # ---- BoneInfo: one per LOD, all identical ------------------------------
    material_count = len(groups)
    transform_indices = [mapping[avatar.hashes.index(h)] for h in required]
    bi = build_bone_info(u.transform_info, transform_indices, material_count)
    u.bone_infos = [bi, bi, bi, bi]

    # ---- streams -----------------------------------------------------------
    mesh_vb = pack_mesh_vertices(mesh['pos'], mesh['nrm'], mesh['uv'],
                                 mesh['bone'], mesh['weight'])
    mesh_ib = mesh['indices'].astype('<u4').tobytes()
    s0 = _clone_stream(ref.stream_infos[0], nv, ni, 1)     # u32 indices
    s1 = _clone_stream(ref.stream_infos[0], nv, ni, 1)
    s2 = _clone_stream(ref.stream_infos[2], len(mesh['cull_b_verts']),
                       len(mesh['cull_b_indices']), 0)
    s3 = _clone_stream(ref.stream_infos[3], len(mesh['cull_a_verts']),
                       len(mesh['cull_a_indices']), 0)
    u.stream_infos = [s0, s1, s2, s3]
    u.stream_info_unk2 = ref.stream_info_unk2
    u.gpu_vertex = [mesh_vb, mesh_vb,
                    pack_cull_vertices(mesh['cull_b_verts'], mesh['cull_b_normals'],
                                       mesh['cull_uv']),
                    pack_cull_vertices(mesh['cull_a_verts'], mesh['cull_a_normals'],
                                       mesh['cull_uv'])]
    # `target` policy: the reference's culling payload is reproduced byte for
    # byte rather than re-packed.  `pack_cull_vertices` can only rebuild what it
    # is given, and the reference's culling meshes carry three UV channels whose
    # second and third we have no values for -- re-packing them would be a silent
    # change to a shell the manager's own rebuilder keeps untouched.
    for i, tag in ((2, 'cull_b'), (3, 'cull_a')):
        raw = mesh.get(tag + '_raw')
        if raw is not None:
            assert len(raw) == len(u.gpu_vertex[i]), \
                '%s raw payload is %d B, re-packed is %d B' % (tag, len(raw),
                                                               len(u.gpu_vertex[i]))
            u.gpu_vertex[i] = raw
    u.gpu_index = [mesh_ib, mesh_ib,
                   np.asarray(mesh['cull_b_indices'], np.uint16).tobytes(),
                   np.asarray(mesh['cull_a_indices'], np.uint16).tobytes()]

    # ---- meshes ------------------------------------------------------------
    # The donor stores four separate LOD meshes (three in stream 1, lod0 in
    # stream 0).  We have one LOD, so all four LOD slots carry the same section
    # table and point at the same block; the patch-level GPU dedupe stores the
    # vertices once.  `unk3` mirrors `transform_index` exactly as the donor does.
    sections = mesh['sections']
    u.mesh_infos = []
    for slot, lod in enumerate((3, 2, 1, 0)):
        tf_idx = lod_tf[lod]
        mi = U.MeshInfo()
        mi.unk1 = 0
        mi.mesh_id = u.transform_info.hashes[tf_idx]
        mi.unk3 = tf_idx
        mi.transform_index = tf_idx
        mi.unk4 = 0
        mi.lod_index = lod
        mi.stream_index = 1 if lod else 0
        mi.unk6 = b'\x00' * 40
        mi.material_offset = 128
        mi.unk8 = 0
        mi.material_ids = [g[1] for g in groups]
        mi.sections = [_mk_section(s) for s in sections]
        # 32 B: our own min/max/radius (see `mesh_bounds`) + the donor's 4
        # unknown bytes for this same LOD slot.
        mi.unk2 = pack_mesh_unk2(mesh_bounds(mesh['pos'], mi.sections),
                                 ref.mesh_infos[slot].unk2[28:32])
        u.mesh_infos.append(mi)
    for name, stream, sid, ref_slot in (('cull_a', 3, 'cull_a', 4),
                                        ('cull_b', 2, 'cull_b', 5)):
        tf_idx = cull_tf[stream]
        cull_verts = mesh['cull_a_verts'] if name == 'cull_a' else mesh['cull_b_verts']
        mi = U.MeshInfo()
        mi.unk1 = 0
        mi.mesh_id = u.transform_info.hashes[tf_idx]
        mi.unk3 = tf_idx
        mi.transform_index = tf_idx
        mi.unk4 = 0
        mi.lod_index = -1
        mi.stream_index = stream
        mi.unk6 = b'\x00' * 40
        mi.material_offset = 128
        mi.unk8 = 0
        mi.material_ids = [LAMBERT1_SHORT_ID]
        sec = U.MeshSectionInfo()
        sec.material_index = 0
        sec.vertex_offset = 0
        sec.num_vertices = len(cull_verts)
        sec.index_offset = 0
        sec.num_indices = (len(mesh['cull_a_indices']) if name == 'cull_a'
                           else len(mesh['cull_b_indices']))
        sec.group_index = 0
        mi.sections = [sec]
        mi.unk2 = pack_mesh_unk2(mesh_bounds(cull_verts, mi.sections),
                                 ref.mesh_infos[ref_slot].unk2[28:32])
        u.mesh_infos.append(mi)

    # ---- the unit's material table ----------------------------------------
    # Four LOD meshes carry the same slots, then the two culling meshes.
    u.section_ids = ([g[1] for g in groups] * 4) + [LAMBERT1_SHORT_ID] * 2
    u.material_ids = ([g[0] for g in groups] * 4) + [LAMBERT1_FILE_ID] * 2

    if mesh_ids is not None and verbose:
        # the caller's generated ids are only a cross-check now: the engine
        # resolves a mesh through its transform node's hash, which the donor
        # already supplies (and which the donor's own 6 meshes confirm).
        want = {lod_tf[lod]: mesh_ids['lod%d' % lod] for lod in (0, 1, 2, 3)}
        want.update({cull_tf[3]: mesh_ids['cull_a'], cull_tf[2]: mesh_ids['cull_b']})
        stale = [tf_idx for tf_idx, mid in want.items()
                 if u.transform_info.hashes[tf_idx] != mid]
        if stale:
            print('  mesh ids: %d generated ids replaced by the donor transform-node '
                  'hashes (the game resolves a mesh through its transform node)'
                  % len(stale))
    return u


def check_unit(u, mesh, avatar):
    """Every invariant the engine relies on, asserted on the built Unit."""
    ti = u.transform_info
    check_transform_info(ti, expect=set(avatar.hashes))

    # mesh id == the hash of the mesh's transform node (donor invariant, all 6)
    for mi in u.mesh_infos:
        assert mi.mesh_id == ti.hashes[mi.transform_index], \
            ('MeshInfo(lod=%d) MeshID 0x%08x is not the hash 0x%08x of its '
             'transform node %d' % (mi.lod_index, mi.mesh_id,
                                    ti.hashes[mi.transform_index], mi.transform_index))

    # the declared bounds must describe the mesh's OWN geometry: no vertex may
    # sit outside the box it publishes, and the radius must be the farthest one
    # (round 59: the donor's stale box left 66290/121704 vertices outside)
    for mi in u.mesh_infos:
        verts = (mesh['cull_a_verts'] if mi.lod_index < 0 and mi.stream_index == 3
                 else mesh['cull_b_verts'] if mi.lod_index < 0
                 else mesh['pos'])
        mn, mx, rad = mesh_bounds(verts, mi.sections)
        stored = struct.unpack_from('<7f', mi.unk2, 0)
        assert np.allclose(stored[0:3], mn, atol=1e-6) and \
            np.allclose(stored[3:6], mx, atol=1e-6) and abs(stored[6] - rad) < 1e-5, \
            ('MeshInfo(lod=%d) publishes %s..%s r=%.6f but its sections draw '
             '%s..%s r=%.6f' % (mi.lod_index, np.round(stored[0:3], 4),
                                np.round(stored[3:6], 4), stored[6],
                                np.round(mn, 4), np.round(mx, 4), rad))

    # every LOD mesh transform must be identity, else the mesh is displaced
    for mi in u.mesh_infos:
        if mi.lod_index < 0:
            continue
        m = np.asarray(ti.matrices[mi.transform_index], np.float64).reshape(4, 4)
        assert np.allclose(m, np.eye(4), atol=1e-6), \
            'LOD%d mesh transform is not identity' % mi.lod_index

    # the palette must resolve, and the inverse bind matrices must be the
    # inverses of the bones' world matrices
    for bi in u.bone_infos:
        assert len(bi.bones) == len(bi.real_indices) == bi.num_bones
        for k, idx in enumerate(bi.real_indices):
            assert 0 <= idx < ti.count, 'real index %d is outside TransformInfo' % idx
            m = np.asarray(ti.matrices[idx], np.float64).reshape(4, 4)
            b = np.asarray(bi.bones[k], np.float64).reshape(4, 4)
            assert np.allclose(b @ m, np.eye(4), atol=1e-5), \
                'bone %d inverse-bind matrix does not invert its node' % k
        for remap in bi.remaps:
            assert len(remap) == bi.num_bones
            assert max(remap) < bi.num_bones

    # the vertex fake indices must be inside the remap of their own material
    nmat = len(u.mesh_infos[0].material_ids)
    bone = mesh['bone']
    assert bone.max() < bi.num_bones, \
        'a vertex fake bone index %d exceeds the palette (%d)' % (bone.max(), bi.num_bones)
    assert nmat == bi.num_remaps, \
        'the LOD mesh has %d material slots but BoneInfo has %d remaps' % (nmat, bi.num_remaps)
    return True


def _mk_section(s):
    sec = U.MeshSectionInfo()
    sec.material_index = s['material_index']
    sec.vertex_offset = s['vertex_offset']
    sec.num_vertices = s['num_vertices']
    sec.index_offset = s['index_offset']
    sec.num_indices = s['num_indices']
    sec.group_index = s['group_index']
    return sec


# ------------------------------------------------------- culling box helpers
def _box_from_world_bounds(bounds_min, bounds_max, transform16):
    """-> (verts_local, min_local, max_local) : an axis-aligned box expressed in
    the local space of `transform16`."""
    inv = np.linalg.inv(np.asarray(transform16, np.float64).reshape(4, 4))
    lo = np.asarray(bounds_min, np.float64)
    hi = np.asarray(bounds_max, np.float64)
    corners = np.array([[x, y, z] for x in (lo[0], hi[0])
                        for y in (lo[1], hi[1]) for z in (lo[2], hi[2])])
    h = np.hstack([corners, np.ones((8, 1))])
    loc = h @ inv
    return loc[:, :3]


def make_culling_box(ref_verts, ref_stream, world_min, world_max, transform16,
                     scale_shape=True):
    """Re-shape a reference culling mesh so it wraps `world_min..world_max`.

    The reference culling meshes are small chamfered boxes (24 verts) whose
    local bounding box the LOD group list stores verbatim -- so we keep the
    topology and just scale it, then report the box we actually produced.
    """
    v = np.asarray(ref_verts, np.float64)
    corners = _box_from_world_bounds(world_min, world_max, transform16)
    lo, hi = corners.min(axis=0), corners.max(axis=0)
    if scale_shape:
        half = (hi - lo) * 0.5
        ctr = (hi + lo) * 0.5
        ref_half = np.abs(v).max(axis=0)
        ref_half = np.where(ref_half < 1e-9, 1.0, ref_half)
        out = ctr + (v / ref_half) * half
    else:
        out = v
    return out, out.min(axis=0), out.max(axis=0)
