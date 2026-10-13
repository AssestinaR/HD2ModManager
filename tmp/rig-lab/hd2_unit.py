# -*- coding: utf-8 -*-
"""Pure-python reader/writer for the HD2 `Unit` asset (TocData + GpuData).

Ported line by line from the community SDK that ships in
`docs/HD2SDK-CommunityEdition-3.8.0`:

    stingray/unit.py  -> StingrayMeshFile, BoneInfo, StreamInfo, MeshInfo,
                         MeshSectionInfo, StreamComponentInfo, TransformInfo
    utils/memoryStream.py -> the byte layout helpers

No Blender, no bpy.  Everything here is byte-exact against the SDK so a package
we write is byte-identical in *layout* to one the SDK writes.

Layout of the TocData (offsets from the start of the payload):

    0x00 u64 UnkRef1
    0x08 u64 BonesRef                 -> Bone asset
    0x10 u64 CompositeRef             -> CompositeUnit asset (0 when written)
    0x18 u64 UnkRef2
    0x20 u64 StateMachineRef
    0x28 u32 HeaderData1
    0x2C u32 Version                  10800437 (old) / 10800438 (current)
    0x30 u32 LODGroupListDataOffset
    0x34 u32 TransformInfoOffset
    0x38 u32 LightListOffset
    0x3C u32 PreLightListOffset
    0x40 u32 WwiseCallbackOffset
    0x44 8B  HeaderData2
    0x4C u32 CustomizationInfoOffset
    0x50 u32 UnkHeaderOffset1
    0x54 u32 ConnectingBoneHashOffset
    0x58 u32 BoneInfoOffset
    0x5C u32 StreamInfoOffset
    0x60 u32 EndingOffset
    0x64 u32 MeshInfoOffset
    0x68 u64 HeaderUnk
    0x70 u32 MaterialsOffset
    0x74 12B padding
    0x80 ...  variable sections

The variable sections are addressed by the offsets above, so a reader never has
to guess their order -- but the *writer* has to emit them in an order where
every "size = next_offset - tell" relationship holds, exactly like the SDK does.
"""
import math
import struct

# --------------------------------------------------------------------- type ids
UNIT_ID = 0xE0A48D0BE9A7453F
BONE_ID = 0x18DEAD01056B72E9
MATERIAL_ID = 0xEAC0B497876ADEDF
TEXTURE_ID = 0xCD4238C6A0C69E32
COMPOSITE_UNIT_ID = 0xC4F0F4BE7FB0C8D6

MAGIC = 4026531857
TYPE_RECORD_SIZE = 32
ENTRY_RECORD_SIZE = 80
TOC_HEADER_SIZE = 72

HEADER_SIZE = 0x80
COMPONENT_AREA = 320
STREAMINFO_HEADER_TAIL = 128     # after the 320-byte component area

# ------------------------------------------------------------ component formats
FMT = {
    'float': 0, 'vec2_float': 1, 'vec3_float': 2, 'vec4_float': 3,
    'rgba_r8g8b8a8': 4, 'vec4_uint32': 24, 'vec4_uint8': 28,
    'vec4_1010102': 29, 'unk_normal': 30, 'vec2_half': 33, 'vec4_half': 35,
}
FMT_OLD = {  # version 10800437
    'float': 0, 'vec2_float': 1, 'vec3_float': 2, 'vec4_float': 3,
    'rgba_r8g8b8a8': 4, 'vec4_uint32': 20, 'vec4_uint8': 24,
    'vec4_1010102': 25, 'unk_normal': 26, 'vec2_half': 29, 'vec4_half': 31,
}
SIZE = {0: 4, 1: 8, 2: 12, 3: 16, 4: 4, 20: 16, 24: 16, 25: 4, 26: 4,
        28: 4, 29: 4, 30: 4, 31: 8, 33: 4, 35: 8}
SIZE_OLD = {0: 4, 1: 8, 2: 12, 3: 16, 4: 4, 20: 16, 24: 4, 25: 4, 26: 4,
            29: 4, 31: 8}
FMT_NAME = {v: k for k, v in FMT.items()}
FMT_NAME_OLD = {v: k for k, v in FMT_OLD.items()}


def format_name(fmt, version):
    return (FMT_NAME_OLD if version == 10800437 else FMT_NAME).get(fmt, '?')
TYPE_NAME = {0: 'position', 1: 'normal', 2: 'tangent', 3: 'bitangent', 4: 'uv',
             5: 'color', 6: 'bone_index', 7: 'bone_weight'}
NAME_TYPE = {v: k for k, v in TYPE_NAME.items()}


def component_size(fmt, version):
    if version == 10800437:
        return SIZE_OLD[fmt]
    return SIZE[fmt]


# ------------------------------------------------------------------- byte views
class Buf:
    """A tiny stand-in for the SDK's MemoryStream: sequential read/write."""

    def __init__(self, data=b''):
        self.d = bytearray(data)
        self.p = 0

    # --- reading
    def u32(self, o=None):
        if o is not None:
            return struct.unpack_from('<I', self.d, o)[0]
        v = struct.unpack_from('<I', self.d, self.p)[0]
        self.p += 4
        return v

    def i32(self, o=None):
        if o is not None:
            return struct.unpack_from('<i', self.d, o)[0]
        v = struct.unpack_from('<i', self.d, self.p)[0]
        self.p += 4
        return v

    def u16(self):
        v = struct.unpack_from('<H', self.d, self.p)[0]
        self.p += 2
        return v

    def u64(self, o=None):
        if o is not None:
            return struct.unpack_from('<Q', self.d, o)[0]
        v = struct.unpack_from('<Q', self.d, self.p)[0]
        self.p += 8
        return v

    def f32s(self, n):
        v = list(struct.unpack_from('<%df' % n, self.d, self.p))
        self.p += 4 * n
        return v

    def raw(self, n):
        v = bytes(self.d[self.p:self.p + n])
        self.p += n
        return v

    def take(self, n, fill=b'\x00'):
        v = bytes(self.d[self.p:self.p + n])
        self.p += n
        if len(v) < n:
            v = v + fill * (n - len(v))
        return v


# =============================================================== sub structures
class BoneInfo:
    def __init__(self):
        self.num_bones = 0
        self.matrix_offset = 0
        self.real_indices_offset = 0
        self.fake_indices_offset = 0
        self.bones = []          # num_bones * 16 floats (4x4, row-major)
        self.real_indices = []   # num_bones * u32  -> index into TransformInfo
        self.num_remaps = 0
        self.remap_offsets = []
        self.remap_counts = []
        self.remaps = []         # num_remaps * [u32]  fake index -> slot

    @staticmethod
    def read(b):
        start = b.p
        bi = BoneInfo()
        bi.num_bones = b.u32()
        bi.matrix_offset = b.u32()
        bi.real_indices_offset = b.u32()
        bi.fake_indices_offset = b.u32()
        b.p = start + bi.matrix_offset
        bi.bones = [b.f32s(16) for _ in range(bi.num_bones)]
        b.p = start + bi.real_indices_offset
        bi.real_indices = [b.u32() for _ in range(bi.num_bones)]
        b.p = start + bi.fake_indices_offset
        bi.num_remaps = b.u32()
        # The SDK interleaves the two arrays: (offset_0, count_0), (offset_1, count_1)...
        # RemapOffsets[0] is `8*NumRemaps+4` and RemapOffsets[i] = RemapOffsets[i-1] + 4*RemapCounts[i]
        # (see BoneInfo.SetRemap), i.e. byte offsets measured from RemapStartPosition.
        remap_start = b.p - 4
        for _ in range(bi.num_remaps):
            bi.remap_offsets.append(b.u32())
            bi.remap_counts.append(b.u32())
        for i in range(bi.num_remaps):
            b.p = remap_start + bi.remap_offsets[i]
            bi.remaps.append([b.u32() for _ in range(bi.remap_counts[i])])
        return bi


class StreamComponentInfo:
    def __init__(self, typ=0, fmt=0, index=0, unknown=0):
        self.type = typ
        self.format = fmt
        self.index = index
        self.unknown = unknown

    @staticmethod
    def read(b):
        return StreamComponentInfo(b.u32(), b.u32(), b.u32(), b.u64())

    def size(self, version):
        return component_size(self.format, version)


class StreamInfo:
    def __init__(self):
        self.component_info_id = 0
        self.components = []
        self.vertex_buffer_id = 0
        self.vertex_buffer_unk1 = 0
        self.num_vertices = 0
        self.vertex_stride = 0
        self.vertex_buffer_unk2 = 0
        self.vertex_buffer_unk3 = 0
        self.index_buffer_id = 0
        self.index_buffer_unk1 = 0
        self.num_indices = 0
        self.index_buffer_type = 0
        self.index_buffer_unk2 = 0
        self.index_buffer_unk3 = 0
        self.vertex_buffer_offset = 0
        self.vertex_buffer_size = 0
        self.index_buffer_offset = 0
        self.index_buffer_size = 0
        self.unk_ending = b'\x00' * 16
        self.comp_area_raw = b'\x00' * COMPONENT_AREA
        self.num_components = 0

    @staticmethod
    def read(b, version):
        si = StreamInfo()
        si.component_info_id = b.u64()
        comp_off = b.p
        b.p = comp_off + COMPONENT_AREA
        si.num_components = b.u64()
        si.vertex_buffer_id = b.u64()
        si.vertex_buffer_unk1 = b.u64()
        si.num_vertices = b.u32()
        si.vertex_stride = b.u32()
        si.vertex_buffer_unk2 = b.u64()
        si.vertex_buffer_unk3 = b.u64()
        si.index_buffer_id = b.u64()
        si.index_buffer_unk1 = b.u64()
        si.num_indices = b.u32()
        si.index_buffer_type = b.u32()
        si.index_buffer_unk2 = b.u64()
        si.index_buffer_unk3 = b.u64()
        si.vertex_buffer_offset = b.u32()
        si.vertex_buffer_size = b.u32()
        si.index_buffer_offset = b.u32()
        si.index_buffer_size = b.u32()
        si.unk_ending = b.raw(16)
        end = comp_off + COMPONENT_AREA + STREAMINFO_HEADER_TAIL
        # Keep the whole 320-byte component area verbatim: exporters sometimes
        # leave a full ABI behind while declaring fewer components (e.g. a
        # culling stream declares 6 but still carries the (6,28) bone_index
        # slot).  Writing it back unchanged keeps the unit byte-identical.
        b.p = comp_off
        si.components = [StreamComponentInfo.read(b) for _ in range(si.num_components)]
        b.p = comp_off
        si.comp_area_raw = b.raw(COMPONENT_AREA)
        b.p = _align(end, 16)
        return si


class MeshSectionInfo:
    def __init__(self):
        self.material_index = 0
        self.vertex_offset = 0
        self.num_vertices = 0
        self.index_offset = 0
        self.num_indices = 0
        self.group_index = 0

    @staticmethod
    def read(b):
        s = MeshSectionInfo()
        s.material_index = b.u32()
        s.vertex_offset = b.u32()
        s.num_vertices = b.u32()
        s.index_offset = b.u32()
        s.num_indices = b.u32()
        s.group_index = b.u32()
        return s


class MeshInfo:
    def __init__(self):
        self.unk1 = 0
        self.unk2 = b'\x00' * 32
        self.mesh_id = 0
        self.unk3 = 0
        self.transform_index = 0
        self.unk4 = 0
        self.lod_index = 0
        self.stream_index = 0
        self.unk6 = b'\x00' * 40
        self.num_materials = 0
        self.material_offset = 0
        self.unk8 = 0
        self.num_sections = 0
        self.sections_offset = 0
        self.material_ids = []
        self.sections = []

    @staticmethod
    def read(b):
        mi = MeshInfo()
        mi.unk1 = b.u64()
        mi.unk2 = b.raw(32)
        mi.mesh_id = b.u32()
        mi.unk3 = b.u32()
        mi.transform_index = b.u32()
        mi.unk4 = b.u32()
        mi.lod_index = b.i32()
        mi.stream_index = b.u32()
        mi.unk6 = b.raw(40)
        mi.num_materials = b.u32()
        mi.material_offset = b.u32()
        mi.unk8 = b.u64()
        mi.num_sections = b.u32()
        mi.sections_offset = b.u32()
        mi.material_ids = [b.u32() for _ in range(mi.num_materials)]
        mi.sections = [MeshSectionInfo.read(b) for _ in range(mi.num_sections)]
        return mi


class TransformInfo:
    def __init__(self):
        self.count = 0
        self.unk = b'\x00' * 12
        self.locals = []        # count * StingrayLocalTransform (64 B each)
        self.matrices = []      # count * 16 floats, translation flat 12..14
        self.entries = []       # count * (u16 incriment, u16 parent)
        self.hashes = []        # count * u32

    @staticmethod
    def read(b, off):
        ti = TransformInfo()
        b.p = off
        ti.count = b.u32()
        ti.unk = b.raw(12)
        for _ in range(ti.count):
            ti.locals.append(dict(rot=b.f32s(9), pos=b.f32s(3), scale=b.f32s(3), dummy=b.f32s(1)))
        # The file stores a ROW-MAJOR 4x4 ("row vector" convention: p' = p @ M),
        # so the translation is at flat[12..14] and flat[15] == 1.
        # Verified against the avatar toc: `boss` -> T=(0,-0.0215,+0.9735), which
        # matches the same asset read by the SDK plugin.  Do NOT transpose here.
        for _ in range(ti.count):
            ti.matrices.append(b.f32s(16))
        for _ in range(ti.count):
            ti.entries.append((b.u16(), b.u16()))
        ti.hashes = [b.u32() for _ in range(ti.count)]
        return ti


def _align(v, n):
    return int(math.ceil(v / float(n))) * n


# ================================================================== the Unit
class Unit:
    """Everything the SDK's StingrayMeshFile holds, plus the raw leftovers."""

    def __init__(self):
        self.unk_ref1 = 0
        self.bones_ref = 0
        self.composite_ref = 0
        self.unk_ref2 = 0
        self.state_machine_ref = 0
        self.header_data1 = 0
        self.version = 10800438
        self.lod_group_list_data_offset = 0
        self.transform_info_offset = 0
        self.light_list_offset = 0
        self.pre_light_list_offset = 0
        self.wwise_callback_offset = 0
        self.header_data2 = b'\x00' * 8
        self.customization_info_offset = 0
        self.unk_header_offset1 = 0
        self.connecting_bone_hash_offset = 0
        self.bone_info_offset = 0
        self.stream_info_offset = 0
        self.ending_offset = 0
        self.mesh_info_offset = 0
        self.header_unk = 0
        self.materials_offset = 0

        self.light_count = 0
        self.light_unk0 = [0, 0, 0]
        self.lights = []
        self.lod_group_list_data = b''
        self.customization_data = b''
        self.unk_header_data1 = b''
        self.connecting_bone_data = b''
        self.pre_light_list_data = b''
        self.wwise_callback_data = b''

        self.bone_infos = []
        self.stream_infos = []
        self.stream_info_unk2 = 0
        self.mesh_infos = []
        self.section_ids = []
        self.material_ids = []
        self.unreversed_data2 = b''
        self.ending_bytes = 0
        self.transform_info = TransformInfo()

        self.gpu_vertex = []      # per stream: bytes
        self.gpu_index = []       # per stream: bytes

    # ------------------------------------------------------------------ read
    @staticmethod
    def read(toc, gpu=b''):
        b = Buf(toc)
        u = Unit()
        u.unk_ref1 = b.u64()
        u.bones_ref = b.u64()
        u.composite_ref = b.u64()
        u.unk_ref2 = b.u64()
        u.state_machine_ref = b.u64()
        u.header_data1 = b.u32()
        u.version = b.u32()
        u.lod_group_list_data_offset = b.u32()
        u.transform_info_offset = b.u32()
        u.light_list_offset = b.u32()
        u.pre_light_list_offset = b.u32()
        u.wwise_callback_offset = b.u32()
        u.header_data2 = b.raw(8)
        u.customization_info_offset = b.u32()
        u.unk_header_offset1 = b.u32()
        u.connecting_bone_hash_offset = b.u32()
        u.bone_info_offset = b.u32()
        u.stream_info_offset = b.u32()
        u.ending_offset = b.u32()
        u.mesh_info_offset = b.u32()
        u.header_unk = b.u64()
        u.materials_offset = b.u32()
        b.p += 12

        # Wwise callback / pre-light-list raw blobs
        if u.wwise_callback_offset > 0:
            b.p = u.wwise_callback_offset
            end = u.pre_light_list_offset or u.light_list_offset
            u.wwise_callback_data = b.raw(max(0, end - b.p))
        if u.pre_light_list_offset > 0:
            b.p = u.pre_light_list_offset
            u.pre_light_list_data = b.raw(max(0, u.light_list_offset - b.p))

        # Light list
        b.p = u.light_list_offset
        u.light_count = b.u32()
        u.light_unk0 = [b.u32(), b.u32(), b.u32()]
        for _ in range(u.light_count):
            u.lights.append(Light.read(b))

        # LOD group list data (raw)
        nxt = [x for x in (u.transform_info_offset, u.customization_info_offset,
                           u.unk_header_offset1, u.connecting_bone_hash_offset,
                           u.bone_info_offset, u.stream_info_offset, u.mesh_info_offset) if x > 0]
        nxt.sort()
        end = nxt[0] if nxt else len(toc)
        u.lod_group_list_data = b.raw(max(0, end - b.p))

        if u.transform_info_offset > 0:
            u.transform_info = TransformInfo.read(b, u.transform_info_offset)

        if u.customization_info_offset > 0:
            b.p = u.customization_info_offset
            nxt = [x for x in (u.unk_header_offset1, u.connecting_bone_hash_offset,
                               u.bone_info_offset, u.stream_info_offset,
                               u.mesh_info_offset) if x > 0]
            nxt.sort()
            u.customization_data = b.raw(max(0, (nxt[0] if nxt else len(toc)) - b.p))

        if u.unk_header_offset1 > 0:
            b.p = u.unk_header_offset1
            nxt = [x for x in (u.connecting_bone_hash_offset, u.bone_info_offset,
                               u.stream_info_offset, u.mesh_info_offset) if x > 0]
            nxt.sort()
            u.unk_header_data1 = b.raw(max(0, (nxt[0] if nxt else len(toc)) - b.p))

        if u.connecting_bone_hash_offset > 0:
            b.p = u.connecting_bone_hash_offset
            nxt = [x for x in (u.bone_info_offset, u.stream_info_offset,
                               u.mesh_info_offset) if x > 0]
            nxt.sort()
            u.connecting_bone_data = b.raw(max(0, (nxt[0] if nxt else len(toc)) - b.p))

        # Bone infos
        b.p = u.bone_info_offset
        n = b.u32()
        offsets = [b.u32() for _ in range(n)]
        for i in range(n):
            b.p = u.bone_info_offset + offsets[i]
            u.bone_infos.append(BoneInfo.read(b))

        # Stream infos
        b.p = u.stream_info_offset
        n = b.u32()
        offsets = [b.u32() for _ in range(n)]
        unks = [b.u32() for _ in range(n)]
        u.stream_info_unk2 = b.u32()
        for i in range(n):
            b.p = u.stream_info_offset + offsets[i]
            u.stream_infos.append(StreamInfo.read(b, u.version))
        u.stream_info_unks = unks

        # Mesh infos
        b.p = u.mesh_info_offset
        n = b.u32()
        offsets = [b.u32() for _ in range(n)]
        unks = [b.u32() for _ in range(n)]
        for i in range(n):
            b.p = u.mesh_info_offset + offsets[i]
            u.mesh_infos.append(MeshInfo.read(b))
        u.mesh_info_unks = unks

        # Materials
        b.p = u.materials_offset
        n = b.u32()
        u.section_ids = [b.u32() for _ in range(n)]
        u.material_ids = [b.u64() for _ in range(n)]

        u.unreversed_data2 = b.raw(max(0, u.ending_offset - b.p))
        u.ending_bytes = b.u64()

        # GPU buffers, per stream
        if gpu:
            gb = Buf(gpu)
            for si in u.stream_infos:
                if si.vertex_buffer_size:
                    gb.p = si.vertex_buffer_offset
                    u.gpu_vertex.append(gb.raw(si.vertex_buffer_size))
                else:
                    u.gpu_vertex.append(b'')
                if si.index_buffer_size:
                    gb.p = si.index_buffer_offset
                    u.gpu_index.append(gb.raw(si.index_buffer_size))
                else:
                    u.gpu_index.append(b'')
        return u

    # ------------------------------------------------------------ vertex decode
    def decode_vertices(self, stream_idx):
        """-> list of dicts, one per vertex, keyed by component type name."""
        si = self.stream_infos[stream_idx]
        raw = self.gpu_vertex[stream_idx]
        out = []
        for v in range(si.num_vertices):
            base = v * si.vertex_stride
            rec = {}
            off = 0
            for c in si.components:
                name = TYPE_NAME.get(c.type, 'unknown%d' % c.type)
                key = name if name != 'uv' else 'uv%d' % c.index
                if name == 'bone_index':
                    key = 'bone_index%d' % c.index
                rec[key] = self._decode_one(raw, base + off, c)
                off += component_size(c.format, self.version)
            out.append(rec)
        return out

    def _decode_one(self, raw, off, c):
        f = c.format
        old = self.version == 10800437
        if f == 0:
            return struct.unpack_from('<f', raw, off)[0]
        if f == 1:
            return list(struct.unpack_from('<2f', raw, off))
        if f == 2:
            return list(struct.unpack_from('<3f', raw, off))
        if f == 3:
            return list(struct.unpack_from('<4f', raw, off))
        if f == 4:
            return list(struct.unpack_from('<4B', raw, off))
        if f == (20 if old else 24):
            return list(struct.unpack_from('<4I', raw, off))
        if f == (24 if old else 28):
            return list(struct.unpack_from('<4B', raw, off))
        if f == (25 if old else 29):
            return struct.unpack_from('<I', raw, off)[0]
        if f == (26 if old else 30):
            return struct.unpack_from('<I', raw, off)[0]
        if f == (29 if old else 33):
            return list(struct.unpack_from('<2e', raw, off))
        if f == (31 if old else 35):
            return list(struct.unpack_from('<4e', raw, off))
        raise ValueError('unhandled component format %d' % f)

    def index_list(self, stream_idx):
        si = self.stream_infos[stream_idx]
        raw = self.gpu_index[stream_idx]
        if si.index_buffer_type == 1:
            return list(struct.unpack_from('<%dI' % (len(raw) // 4), raw, 0))
        return list(struct.unpack_from('<%dH' % (len(raw) // 2), raw, 0))


class Light:
    OMNI, SPOT, BOX, DIRECTIONAL = 0, 1, 2, 3

    def __init__(self):
        self.name_hash = 0
        self.bone_index = 0
        self.color = [0.0, 0.0, 0.0]
        self.intensity = 1.0
        self.falloff_start = 0.0
        self.falloff_end = 0.0
        self.falloff_exp = 1.0
        self.start_angle = 0.0
        self.end_angle = 0.0
        self.unk0 = 0.0
        self.shadow_bias = 0.4
        self.unk1 = [0.0] * 5
        self.flags = 0
        self.light_type = 0
        self.unk2 = b'\x00' * 32

    @staticmethod
    def read(b):
        li = Light()
        li.name_hash = b.u32()
        li.bone_index = b.u32()
        li.color = b.f32s(3)
        li.intensity = b.f32s(1)[0]
        li.falloff_start = b.f32s(1)[0]
        li.falloff_end = b.f32s(1)[0]
        li.falloff_exp = b.f32s(1)[0]
        li.start_angle = b.f32s(1)[0]
        li.end_angle = b.f32s(1)[0]
        li.unk0 = b.f32s(1)[0]
        li.shadow_bias = b.f32s(1)[0]
        li.unk1 = b.f32s(5)
        li.flags = b.raw(1)[0]
        b.p += 3
        li.light_type = b.u32()
        li.unk2 = b.raw(32)
        return li

    SIZE = 4 + 4 + 12 + 4 * 8 + 20 + 4 + 4 + 32
