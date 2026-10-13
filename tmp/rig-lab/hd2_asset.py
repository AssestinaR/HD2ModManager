# -*- coding: utf-8 -*-
"""Pure-python reader/writer for the HD2 `Material` and `Texture` assets.

Ported from the community SDK (`stingray/material.py`, `stingray/texture.py`).
Byte layout, verified against real assets in a shipping patch:

Material TocData
    12B  undat1
    u32  EndOffset
    u64  undat2
    u64  ParentMaterialID        -> one of Global_MaterialParentIDs
    32B  undat3
    u32  NumTextures
    36B  undat4
    u32  NumVariables
    12B  undat5
    u32  VariableDataSize
    12B  undat6
    NumTextures * (u32 TexUnk + u64 TexID)
    NumVariables * (u32 klass, u32 elements, u32 ID, u32 offset, u32 elementStride)
    <remaining bytes = the shader-variable value area>
    then each variable's value is read at (value-area start + variable.offset),
    `klass+1` float32s.

Texture TocData (340 bytes)
    u32 UnkID, u32 Unk1(0), u32 Unk2(0xFFFFFFFF)
    15 * StingrayMipmapInfo (u32 Start, u32 BytesLeft, u16 Height, u16 Width)
    148B DDS header, which MUST carry the DX10 extension
Pixel bytes live in the GPU resource (the mipmap table is written cleared).
"""
import struct

MATERIAL_TYPE_ID = 0xEAC0B497876ADEDF
TEXTURE_TYPE_ID = 0xCD4238C6A0C69E32

# stingray/constants.py :: Global_MaterialParentIDs
MATERIAL_PARENTS = {
    3430705909399566334: 'basic+',
    15586118709890920288: 'alphaclip',
    6101987038150196875: 'original',
    15356477064658408677: 'basic',
    15235712479575174153: 'emissive',
    17265463703140804126: 'advanced',
    17720495965476876300: 'armorlut',
    9576304397847579354: 'translucent',
    8580182439406660688: 'basic+',
}

KLASS_NAMES = {0: 'Scalar', 1: 'Vector2', 2: 'Vector3', 3: 'Vector4', 12: 'Other'}

TEXTURE_TOC_SIZE = 12 + 15 * 12 + 148      # 340
DDS_HEADER_SIZE = 148
DXGI_NAME = {
    70: 'BC1_TYPELESS', 71: 'BC1_UNORM', 72: 'BC1_UNORM_SRGB',
    73: 'BC2_TYPELESS', 74: 'BC2_UNORM', 75: 'BC2_UNORM_SRGB',
    76: 'BC3_TYPELESS', 77: 'BC3_UNORM', 78: 'BC3_UNORM_SRGB',
    79: 'BC4_TYPELESS', 80: 'BC4_UNORM', 81: 'BC4_SNORM',
    82: 'BC5_TYPELESS', 83: 'BC5_UNORM', 84: 'BC5_SNORM',
    87: 'B8G8R8A8_UNORM', 88: 'B8G8R8X8_UNORM', 28: 'R8G8B8A8_UNORM',
    29: 'R8G8B8A8_UNORM_SRGB',
}
BC_BLOCK_BYTES = {70: 8, 71: 8, 72: 8, 73: 16, 74: 16, 75: 16,
                  76: 16, 77: 16, 78: 16, 79: 8, 80: 8, 81: 8,
                  82: 16, 83: 16, 84: 16}


class ShaderVariable:
    def __init__(self):
        self.klass = 0
        self.elements = 0
        self.id = 0
        self.offset = 0
        self.element_stride = 0
        self.values = []
        self.name = None

    @property
    def klass_name(self):
        return KLASS_NAMES.get(self.klass, 'Other')

    @property
    def n_floats(self):
        return self.klass + 1


class Material:
    def __init__(self):
        self.undat1 = b'\x00' * 12
        self.end_offset = 0
        self.undat2 = 0
        self.parent_material_id = 0
        self.undat3 = b'\x00' * 32
        self.undat4 = b'\x00' * 36
        self.undat5 = b'\x00' * 12
        self.undat6 = b'\x00' * 12
        self.variable_data_size = 0
        self.tex_unks = []
        self.tex_ids = []
        self.variables = []
        self.remaining = b''

    @property
    def parent_name(self):
        return MATERIAL_PARENTS.get(self.parent_material_id, '0x%x' % self.parent_material_id)

    @staticmethod
    def read(data):
        m = Material()
        p = 0
        m.undat1 = data[0:12]; p = 12
        m.end_offset, = struct.unpack_from('<I', data, p); p += 4
        m.undat2, = struct.unpack_from('<Q', data, p); p += 8
        m.parent_material_id, = struct.unpack_from('<Q', data, p); p += 8
        m.undat3 = data[p:p + 32]; p += 32
        ntex, = struct.unpack_from('<I', data, p); p += 4
        m.undat4 = data[p:p + 36]; p += 36
        nvar, = struct.unpack_from('<I', data, p); p += 4
        m.undat5 = data[p:p + 12]; p += 12
        m.variable_data_size, = struct.unpack_from('<I', data, p); p += 4
        m.undat6 = data[p:p + 12]; p += 12
        for _ in range(ntex):
            tu, = struct.unpack_from('<I', data, p)
            tid, = struct.unpack_from('<Q', data, p + 4)
            m.tex_unks.append(tu)
            m.tex_ids.append(tid)
            p += 12
        for _ in range(nvar):
            klass, elems, vid, off, stride = struct.unpack_from('<5I', data, p)
            v = ShaderVariable()
            v.klass, v.elements, v.id, v.offset, v.element_stride = klass, elems, vid, off, stride
            m.variables.append(v)
            p += 20
        m.remaining = data[p:]
        for v in m.variables:
            v.values = [struct.unpack_from('<f', m.remaining, v.offset + 4 * k)[0]
                        for k in range(v.n_floats)]
        return m

    def write(self):
        out = bytearray()
        out += self.undat1
        out += struct.pack('<I', self.end_offset)
        out += struct.pack('<Q', self.undat2)
        out += struct.pack('<Q', self.parent_material_id)
        out += self.undat3
        out += struct.pack('<I', len(self.tex_ids))
        out += self.undat4
        out += struct.pack('<I', len(self.variables))
        out += self.undat5
        out += struct.pack('<I', self.variable_data_size)
        out += self.undat6
        for tu, tid in zip(self.tex_unks, self.tex_ids):
            out += struct.pack('<I', tu) + struct.pack('<Q', tid)
        for v in self.variables:
            out += struct.pack('<5I', v.klass, v.elements, v.id, v.offset, v.element_stride)
        # rebuild the value area, keeping every variable's offset byte-identical
        body = bytearray(self.remaining)
        need = max((v.offset + 4 * v.n_floats for v in self.variables), default=0)
        if len(body) < need:
            body.extend(b'\x00' * (need - len(body)))
        for v in self.variables:
            for k in range(v.n_floats):
                struct.pack_into('<f', body, v.offset + 4 * k, v.values[k])
        out += body
        # `EndOffset` is copied verbatim -- do NOT recompute it.
        # The SDK treats it as an opaque uint32 and never rewrites it
        # (stingray/material.py), and the real files prove it is not
        # `136 + 12*ntex + 20*nvar`: every SDK template has
        # `EndOffset == size - 36`, while the shipping Chiffon material
        # 0x0228cdef42ee1e6e has `EndOffset == size - 32`.  The naive formula
        # would silently corrupt the byte right after the header for any
        # material whose template does not happen to match it.
        return bytes(out)

    def set_texture(self, index, tex_id):
        while len(self.tex_ids) <= index:
            self.tex_unks.append(0)
            self.tex_ids.append(0)
        self.tex_ids[index] = tex_id

    def find_variable(self, name):
        for v in self.variables:
            if v.name == name:
                return v
        return None


class Texture:
    def __init__(self):
        self.unk_id = 0
        self.unk1 = 0
        self.unk2 = 0xFFFFFFFF
        self.mipmap_info = [(0, 0, 0, 0)] * 15
        self.dds_header = bytearray(DDS_HEADER_SIZE)
        self.pixels = b''

    @property
    def width(self):
        return struct.unpack_from('<I', self.dds_header, 16)[0]

    @property
    def height(self):
        return struct.unpack_from('<I', self.dds_header, 12)[0]

    @property
    def mip_count(self):
        return struct.unpack_from('<I', self.dds_header, 28)[0]

    @property
    def dxgi_format(self):
        return struct.unpack_from('<I', self.dds_header, 128)[0]

    @property
    def array_size(self):
        return struct.unpack_from('<I', self.dds_header, 140)[0]

    @property
    def format_name(self):
        return DXGI_NAME.get(self.dxgi_format, 'DXGI_%d' % self.dxgi_format)

    @staticmethod
    def read(toc, gpu=b''):
        t = Texture()
        t.unk_id, t.unk1, t.unk2 = struct.unpack_from('<3I', toc, 0)
        p = 12
        t.mipmap_info = [struct.unpack_from('<IIHH', toc, p + i * 12) for i in range(15)]
        p += 180
        t.dds_header = bytearray(toc[p:p + DDS_HEADER_SIZE])
        if t.dds_header[84:88] != b'DX10':
            raise ValueError('DDS must use the DX10 extended header, got %r' % t.dds_header[84:88])
        t.pixels = gpu
        return t

    def to_dds(self):
        return bytes(self.dds_header) + self.pixels

    def from_dds(self, dds):
        if dds[84:88] != b'DX10':
            raise ValueError('DDS must use the DX10 extended header')
        self.dds_header = bytearray(dds[:DDS_HEADER_SIZE])
        self.pixels = dds[DDS_HEADER_SIZE:]

    def write_toc(self):
        out = bytearray()
        out += struct.pack('<3I', self.unk_id, 0, 0xFFFFFFFF)
        for _ in range(15):
            out += struct.pack('<IIHH', 0, 0, 0, 0)     # cleared, exactly like the SDK
        out += self.dds_header
        return bytes(out)


# ------------------------------------------------------------------ DDS helper
def build_dds_dx10(width, height, dxgi_format, mip_chain, array_size=1):
    """mip_chain: list of raw mip0..mipN byte blobs (already BC-compressed)."""
    block = BC_BLOCK_BYTES.get(dxgi_format)
    if block is None:
        raise ValueError('unsupported DXGI format %d' % dxgi_format)
    flags = 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000       # caps|height|width|pitch|linearsize
    if len(mip_chain) > 1:
        flags |= 0x20000                              # mipmapcount
    hdr = bytearray(DDS_HEADER_SIZE)
    struct.pack_into('<4s', hdr, 0, b'DDS ')
    struct.pack_into('<I', hdr, 4, 124)
    struct.pack_into('<I', hdr, 8, flags)
    struct.pack_into('<I', hdr, 12, height)
    struct.pack_into('<I', hdr, 16, width)
    struct.pack_into('<I', hdr, 20, max(1, (width + 3) // 4) * block)
    struct.pack_into('<I', hdr, 24, 0)
    struct.pack_into('<I', hdr, 28, len(mip_chain))
    struct.pack_into('<I', hdr, 76, 32)               # pixelformat size
    struct.pack_into('<I', hdr, 80, 0x4)              # DDPF_FOURCC
    struct.pack_into('<4s', hdr, 84, b'DX10')
    struct.pack_into('<I', hdr, 108, 0x1000)          # DDSCAPS_TEXTURE
    struct.pack_into('<I', hdr, 128, dxgi_format)
    struct.pack_into('<I', hdr, 132, 3)               # D3D10_RESOURCE_DIMENSION_TEXTURE2D
    struct.pack_into('<I', hdr, 136, 0)               # miscFlag
    struct.pack_into('<I', hdr, 140, array_size)
    struct.pack_into('<I', hdr, 144, 0)               # miscFlags2
    return bytes(hdr) + b''.join(mip_chain)
