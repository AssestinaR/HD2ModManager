# -*- coding: utf-8 -*-
"""FROZEN reproduction of the pre-fix `hd2_asset.Material` texture-table code.

Kept deliberately as a negative control.  The bug it encodes is that the
texture table was read/written as N interleaved `(u32 key, u64 id)` records
while the SDK (`stingray/material.py:32-33`) uses two contiguous arrays
(`N x u32` keys, then `N x u64` ids, ids starting at `136 + 4*N`).

Why this file exists: a self-consistent reader/writer roundtrip CANNOT detect
the bug -- writing then reading with the same wrong layout returns the ids you
meant to write.  Only an external authority (the SDK parser, or the manager's
`StingrayMaterialReferenceReader.cs`) exposes it.  So the negative control must
be a frozen copy of the old code, never the live module.

Do NOT use this module to build anything.  It exists only so a test can prove
"the old layout is rejected by the SDK" even after the live code is fixed.
"""
import struct

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


class LegacyShaderVariable:
    def __init__(self):
        self.klass = self.elements = self.id = self.offset = self.element_stride = 0
        self.values = []

    @property
    def n_floats(self):
        return self.klass + 1


class LegacyMaterial:
    """Byte-for-byte the old (buggy) `hd2_asset.Material`."""

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
        m = LegacyMaterial()
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
        for _ in range(ntex):                      # <-- the bug
            tu, = struct.unpack_from('<I', data, p)
            tid, = struct.unpack_from('<Q', data, p + 4)
            m.tex_unks.append(tu)
            m.tex_ids.append(tid)
            p += 12
        for _ in range(nvar):
            klass, elems, vid, off, stride = struct.unpack_from('<5I', data, p)
            v = LegacyShaderVariable()
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
        for tu, tid in zip(self.tex_unks, self.tex_ids):   # <-- the bug
            out += struct.pack('<I', tu) + struct.pack('<Q', tid)
        for v in self.variables:
            out += struct.pack('<5I', v.klass, v.elements, v.id, v.offset, v.element_stride)
        body = bytearray(self.remaining)
        need = max((v.offset + 4 * v.n_floats for v in self.variables), default=0)
        if len(body) < need:
            body.extend(b'\x00' * (need - len(body)))
        for v in self.variables:
            for k in range(v.n_floats):
                struct.pack_into('<f', body, v.offset + 4 * k, v.values[k])
        out += body
        return bytes(out)

    def set_texture(self, index, tex_id):
        while len(self.tex_ids) <= index:
            self.tex_unks.append(0)
            self.tex_ids.append(0)
        self.tex_ids[index] = tex_id
