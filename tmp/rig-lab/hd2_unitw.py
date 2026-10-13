# -*- coding: utf-8 -*-
"""Byte-exact *writer* for the HD2 `Unit` asset.

Mirrors the SDK's `StingrayMeshFile.Serialize` + `SerializeGpuData` ordering
(`docs/HD2SDK-CommunityEdition-3.8.0/stingray/unit.py`).  The reader lives in
`hd2_unit.py`; this module is the inverse and is validated by a read->write
round-trip that must reproduce the source TocData byte for byte.

Write order (this is load-bearing -- every section length in the file is
"next offset - tell", so the order must match exactly):

    0x00  header (0x80 bytes, patched at the end)
    0x80  WwiseCallbackData        (only if non-empty)
          PreLightListData         (only if non-empty)
          LightList                (u32 count + 3*u32 + count*Light)
          LODGroupListData         -> LODGroupListDataOffset
          TransformInfo            -> TransformInfoOffset, then pad to 16
          CustomizationData        -> CustomizationInfoOffset
          UnkHeaderData1           -> UnkHeaderOffset1
          ConnectingBoneData       -> ConnectingBoneHashOffset
          BoneInfo block           -> BoneInfoOffset
          [pad to 16]
          StreamInfo block         -> StreamInfoOffset
          MeshInfo block           -> MeshInfoOffset
          Materials                -> MaterialsOffset
          UnreversedData2
                                   -> EndingOffset
          u64 EndingBytes (= NumMeshes)

ALWAYS call `serialize_gpu(u)` before `serialize_unit(u)`: the GPU pass fills in
each StreamInfo's VertexBufferOffset/Size and IndexBufferOffset/Size.
"""
import math
import struct

import hd2_unit as U

HEADER_SIZE = U.HEADER_SIZE          # 0x80
COMPONENT_AREA = U.COMPONENT_AREA    # 320


class W:
    """Growable little-endian byte buffer with a real write position.

    Unlike a plain `bytearray` this supports the SDK's `seek(); write()`
    pattern: writing after a seek overwrites in place (extending as needed),
    which is what makes the 320-byte component area and the offset tables work.
    """

    def __init__(self):
        self.b = bytearray()
        self.p = 0

    def tell(self):
        return self.p

    def seek(self, pos):
        self.p = int(pos)
        if self.p > len(self.b):
            self.b.extend(b'\x00' * (self.p - len(self.b)))

    def _put(self, data):
        data = bytes(data)
        end = self.p + len(data)
        if end > len(self.b):
            self.b.extend(b'\x00' * (end - len(self.b)))
        self.b[self.p:end] = data
        self.p = end

    def u32(self, v):
        self._put(struct.pack('<I', int(v) & 0xffffffff))

    def i32(self, v):
        self._put(struct.pack('<i', int(v)))

    def u16(self, v):
        self._put(struct.pack('<H', int(v) & 0xffff))

    def u64(self, v):
        self._put(struct.pack('<Q', int(v) & 0xffffffffffffffff))

    def f32s(self, vals):
        self._put(struct.pack('<%df' % len(vals), *[float(x) for x in vals]))

    def raw(self, data):
        self._put(data)


def align16(v):
    return int(math.ceil(v / 16.0)) * 16


# ------------------------------------------------------------------ sections
def write_transform_info(w, ti):
    """u32 count + 12B + locals + matrices + entries + hashes."""
    w.u32(ti.count)
    w.raw(ti.unk if len(ti.unk) == 12 else b'\x00' * 12)
    for lo in ti.locals:
        w.f32s(lo['rot'])
        w.f32s(lo['pos'])
        w.f32s(lo['scale'])
        w.f32s(lo['dummy'])
    for m in ti.matrices:
        w.f32s(m)
    for (inc, par) in ti.entries:
        w.u16(inc)
        w.u16(par)
    for h in ti.hashes:
        w.u32(h)


def remap_offsets_for(bi):
    """Byte offsets of each remap array, relative to the remap block start."""
    if len(bi.remap_offsets) == bi.num_remaps:
        return list(bi.remap_offsets)
    offs = [8 * bi.num_remaps + 4]
    for i in range(1, bi.num_remaps):
        offs.append(offs[i - 1] + 4 * bi.remap_counts[i - 1])
    return offs


def write_bone_info(w, bi):
    """BoneInfo.Serialize_REAL -- the three offsets are relative to the start."""
    start = w.tell()
    w.u32(bi.num_bones)
    w.u32(0)   # matrix_offset        (patched)
    w.u32(0)   # real_indices_offset  (patched)
    w.u32(0)   # fake_indices_offset  (patched)
    matrix_offset = w.tell() - start
    for m in bi.bones:
        w.f32s(m)
    real_offset = w.tell() - start
    for i in bi.real_indices:
        w.u32(i)
    fake_offset = w.tell() - start
    w.u32(bi.num_remaps)
    offs = remap_offsets_for(bi)
    for i in range(bi.num_remaps):
        w.u32(offs[i])
        w.u32(bi.remap_counts[i])
    for i in range(bi.num_remaps):
        for x in bi.remaps[i]:
            w.u32(x)
    struct.pack_into('<I', w.b, start + 4, matrix_offset)
    struct.pack_into('<I', w.b, start + 8, real_offset)
    struct.pack_into('<I', w.b, start + 12, fake_offset)


def write_stream_info(w, si):
    """8B id + 320B component area + 120B tail, then pad to an absolute 16."""
    w.u64(si.component_info_id)
    comp_off = w.tell()
    w.seek(comp_off + COMPONENT_AREA)
    w.u64(len(si.components))
    w.u64(si.vertex_buffer_id)
    w.u64(si.vertex_buffer_unk1)
    w.u32(si.num_vertices)
    w.u32(si.vertex_stride)
    w.u64(si.vertex_buffer_unk2)
    w.u64(si.vertex_buffer_unk3)
    w.u64(si.index_buffer_id)
    w.u64(si.index_buffer_unk1)
    w.u32(si.num_indices)
    w.u32(si.index_buffer_type)
    w.u64(si.index_buffer_unk2)
    w.u64(si.index_buffer_unk3)
    w.u32(si.vertex_buffer_offset)
    w.u32(si.vertex_buffer_size)
    w.u32(si.index_buffer_offset)
    w.u32(si.index_buffer_size)
    w.raw(si.unk_ending if len(si.unk_ending) == 16 else b'\x00' * 16)
    end_offset = align16(w.tell())
    w.seek(comp_off)
    raw = si.comp_area_raw if len(getattr(si, 'comp_area_raw', b'')) == COMPONENT_AREA \
        else b'\x00' * COMPONENT_AREA
    w.raw(raw)
    w.seek(comp_off)
    for c in si.components:
        w.u32(c.type)
        w.u32(c.format)
        w.u32(c.index)
        w.u64(c.unknown)
    w.seek(end_offset)


def write_mesh_info(w, mi):
    """MeshInfo.Serialize.

    `MaterialOffset` is *not* recomputed: the SDK writes the value it read, and
    it is always 128 (the size of the fixed part of the MeshInfo header) because
    the material IDs follow the header inline.  `SectionsOffset` is then
    `MaterialOffset + 4*NumMaterials`.
    """
    start = w.tell()
    w.u64(mi.unk1)
    w.raw(mi.unk2 if len(mi.unk2) == 32 else b'\x00' * 32)
    w.u32(mi.mesh_id)
    w.u32(mi.unk3)
    w.u32(mi.transform_index)
    w.u32(mi.unk4)
    w.i32(mi.lod_index)
    w.u32(mi.stream_index)
    w.raw(mi.unk6 if len(mi.unk6) == 40 else b'\x00' * 40)
    w.u32(len(mi.material_ids))
    material_offset = mi.material_offset if mi.material_offset else 128
    w.u32(material_offset)
    w.u64(mi.unk8)
    w.u32(len(mi.sections))
    w.u32(material_offset + 4 * len(mi.material_ids))
    for x in mi.material_ids:          # per-mesh material slot short IDs
        w.u32(x)
    for s in mi.sections:
        w.u32(s.material_index)
        w.u32(s.vertex_offset)
        w.u32(s.num_vertices)
        w.u32(s.index_offset)
        w.u32(s.num_indices)
        w.u32(s.group_index)


# ---------------------------------------------------------------------- unit
def serialize_unit(u):
    """-> TocData bytes.  `u` is an `hd2_unit.Unit` (read or hand-built)."""
    w = W()
    w.raw(b'\x00' * HEADER_SIZE)

    off = {}
    if u.wwise_callback_data:
        off['wwise'] = w.tell()
        w.raw(u.wwise_callback_data)
    if u.pre_light_list_data:
        off['pre'] = w.tell()
        w.raw(u.pre_light_list_data)

    off['light'] = w.tell()
    w.u32(u.light_count)
    for x in (list(u.light_unk0) + [0, 0, 0])[:3]:
        w.u32(x)
    for li in u.lights:
        w.u32(li.name_hash)
        w.u32(li.bone_index)
        w.f32s(li.color)
        w.f32s([li.intensity, li.falloff_start, li.falloff_end, li.falloff_exp,
                li.start_angle, li.end_angle, li.unk0, li.shadow_bias])
        w.f32s(li.unk1)
        w.raw(bytes([li.flags & 0xff, 0, 0, 0]))
        w.u32(li.light_type)
        w.raw(li.unk2 if len(li.unk2) == 32 else b'\x00' * 32)

    off['lod'] = w.tell()
    w.raw(u.lod_group_list_data)

    if u.transform_info.count:
        off['tf'] = w.tell()
        write_transform_info(w, u.transform_info)
        w.seek(align16(w.tell()))

    if u.customization_data:
        off['cust'] = w.tell()
        w.raw(u.customization_data)
    if u.unk_header_data1:
        off['unkhdr1'] = w.tell()
        w.raw(u.unk_header_data1)
    if u.connecting_bone_data:
        off['conn'] = w.tell()
        w.raw(u.connecting_bone_data)

    off['boneinfo'] = w.tell()
    w.u32(len(u.bone_infos))
    at = w.tell()
    w.seek(at + 4 * len(u.bone_infos))
    for i, bi in enumerate(u.bone_infos):
        struct.pack_into('<I', w.b, at + 4 * i, w.tell() - off['boneinfo'])
        write_bone_info(w, bi)

    if u.stream_infos:
        w.seek(align16(w.tell()))
        off['streaminfo'] = w.tell()
        n = len(u.stream_infos)
        w.u32(n)
        at = w.tell()
        w.seek(at + 8 * n + 4)          # offsets[] + unks[] + unk2
        offs = []
        for si in u.stream_infos:
            offs.append(w.tell() - off['streaminfo'])
            write_stream_info(w, si)
        for i, o in enumerate(offs):
            struct.pack_into('<I', w.b, at + 4 * i, o)
        for i in range(n):
            struct.pack_into('<I', w.b, at + 4 * n + 4 * i,
                             u.mesh_infos[i].mesh_id if i < len(u.mesh_infos) else 0)
        struct.pack_into('<I', w.b, at + 8 * n, u.stream_info_unk2)

    off['meshinfo'] = w.tell()
    n = len(u.mesh_infos)
    w.u32(n)
    at = w.tell()
    w.seek(at + 8 * n)                  # offsets[] + unks[]
    for i, mi in enumerate(u.mesh_infos):
        struct.pack_into('<I', w.b, at + 4 * i, w.tell() - off['meshinfo'])
        write_mesh_info(w, mi)
    for i, mi in enumerate(u.mesh_infos):
        struct.pack_into('<I', w.b, at + 4 * n + 4 * i, mi.mesh_id)   # MeshInfoUnk[i]

    off['materials'] = w.tell()
    w.u32(len(u.material_ids))
    for x in u.section_ids:
        w.u32(x)
    for x in u.material_ids:
        w.u64(x)

    w.raw(u.unreversed_data2)
    off['ending'] = w.tell()
    w.u64(n)

    patch_header(w, u, off)
    return bytes(w.b)


def patch_header(w, u, off):
    def p32(o, v):
        struct.pack_into('<I', w.b, o, int(v) & 0xffffffff)

    def p64(o, v):
        struct.pack_into('<Q', w.b, o, int(v) & 0xffffffffffffffff)

    p64(0x00, u.unk_ref1)
    p64(0x08, u.bones_ref)
    p64(0x10, 0)                       # CompositeRef is always 0 when writing
    p64(0x18, u.unk_ref2)
    p64(0x20, u.state_machine_ref)
    p32(0x28, u.header_data1)
    p32(0x2C, u.version)
    p32(0x30, off.get('lod', 0))
    p32(0x34, off.get('tf', 0))
    p32(0x38, off.get('light', 0))
    p32(0x3C, off.get('pre', 0))
    p32(0x40, off.get('wwise', 0))
    w.b[0x44:0x4C] = u.header_data2 if len(u.header_data2) == 8 else b'\x00' * 8
    p32(0x4C, off.get('cust', 0))
    p32(0x50, off.get('unkhdr1', 0))
    p32(0x54, off.get('conn', 0))
    p32(0x58, off.get('boneinfo', 0))
    p32(0x5C, off.get('streaminfo', 0))
    p32(0x60, off.get('ending', 0))
    p32(0x64, off.get('meshinfo', 0))
    p64(0x68, u.header_unk)
    p32(0x70, off.get('materials', 0))


# ---------------------------------------------------------------------- gpu
def serialize_gpu(u):
    """-> GpuData bytes; fills each StreamInfo's offsets/sizes in place.

    Layout: for each stream, the vertex buffer (nverts*stride) padded to an
    *absolute* 16-byte boundary, then the index buffer (no padding).
    `VertexBufferSize` therefore includes the padding and `IndexBufferOffset`
    is the aligned end of the vertex buffer -- exactly what the SDK does.
    """
    g = W()
    for i, si in enumerate(u.stream_infos):
        si.vertex_buffer_offset = g.tell()
        g.raw(u.gpu_vertex[i] if i < len(u.gpu_vertex) else b'')
        g.seek(align16(g.tell()))
        si.vertex_buffer_size = g.tell() - si.vertex_buffer_offset
        si.index_buffer_offset = g.tell()
        g.raw(u.gpu_index[i] if i < len(u.gpu_index) else b'')
        si.index_buffer_size = g.tell() - si.index_buffer_offset
    return bytes(g.b)
