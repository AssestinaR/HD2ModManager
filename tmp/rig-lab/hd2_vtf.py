# -*- coding: utf-8 -*-
"""Minimal Valve VTF (7.x) reader -> DX10 DDS.

Only what the offline lab needs: pull the high-res mip chain out of a VTF so it
can be wrapped in a DX10 DDS that the HD2 `Texture` asset accepts.

Verified layouts (measured on the Lunalice packs):

    v7.2  headerSize 80      v7.4  headerSize 96
    +0   u32  signature  b'VTF\\0'
    +4   u32  major        +8  u32 minor
    +12  u32  headerSize
    +16  u16  width        +18 u16 height
    +20  u32  flags        +24 u16 frames   +26 u16 firstFrame
    +32  f32[4] reflectivity  +48 f32 bumpScale
    +52  u32  highResFormat   +56 u8 mipCount
    +57  u8   lowResFormat    +61 u8 lowResWidth  +62 u8 lowResHeight
    +63  u16  depth

(offsets measured; note reflectivity starts at 32, not 28, and highResFormat at
52, not 48 -- 4 bytes sit between firstFrame and reflectivity.)

The image data begins exactly at `headerSize` and the mip chain is *sequential*
with no resource directory in between (checked by exact size arithmetic on
2048x2048/10-mip, 1024x1024/1-mip, 1x1 RGB888 and 512x512 mask files).  The
low-res thumbnail sits after the chain; its size is
`lowresWidth*lowresHeight*bytesPerPixel(lowResFormat)` (block formats use the
block size).
"""
import struct

# ---------------------------------------------------------------- VTF formats
VTF_FORMATS = {
    0: 'RGBA8888', 1: 'ABGR8888', 2: 'RGB888', 3: 'BGR888', 4: 'RGB565',
    5: 'I8', 6: 'IA88', 7: 'P8', 8: 'A8', 9: 'RGB888_BLUESCREEN',
    10: 'BGR888_BLUESCREEN', 11: 'ARGB8888', 12: 'BGRA8888', 13: 'DXT1',
    14: 'DXT3', 15: 'DXT5', 16: 'BGRX8888', 17: 'BGR565', 18: 'BGRX5551',
    19: 'BGRA4444', 20: 'DXT1_ONEBITALPHA', 21: 'BGRA5551', 22: 'UV88',
    23: 'UVWQ8888', 24: 'RGBA16161616F', 25: 'RGBA16161616', 26: 'UVLX8888',
    # non-standard L4D2/CSGO-era compressed masks:
    #   37 -> 16 bytes / 4x4 block = BC5 / ATI2N   (the `_n` normal maps)
    #   38 ->  8 bytes / 4x4 block = BC4 / ATI1N   (single channel masks)
    37: 'BC5_ATI2N', 38: 'BC4_ATI1N',
}

# (bytes-per-pixel, block-bytes-or-0, block-side)
VTF_FMT_INFO = {
    0: (4, 0, 0), 1: (4, 0, 0), 2: (3, 0, 0), 3: (3, 0, 0), 4: (2, 0, 0),
    5: (1, 0, 0), 6: (2, 0, 0), 7: (1, 0, 0), 8: (1, 0, 0),
    9: (3, 0, 0), 10: (3, 0, 0), 11: (4, 0, 0), 12: (4, 0, 0),
    13: (0, 8, 4), 14: (0, 16, 4), 15: (0, 16, 4),
    16: (4, 0, 0), 17: (2, 0, 0), 18: (2, 0, 0), 19: (2, 0, 0),
    20: (0, 8, 4), 21: (2, 0, 0), 22: (2, 0, 0), 23: (4, 0, 0),
    24: (8, 0, 0), 25: (8, 0, 0), 26: (4, 0, 0),
    37: (0, 16, 4), 38: (0, 8, 4),
}

# VTF format -> DXGI format (only the BC ones are exact; the rest go via texconv)
VTF_TO_DXGI = {
    13: 72,   # DXT1  -> BC1_UNORM_SRGB
    20: 72,   # DXT1_ONEBITALPHA -> BC1_UNORM_SRGB
    14: 75,   # DXT3  -> BC2_UNORM_SRGB
    15: 78,   # DXT5  -> BC3_UNORM_SRGB
    37: 83,   # BC5   -> BC5_UNORM  (linear: normal maps)
    38: 80,   # BC4   -> BC4_UNORM  (linear: masks)
}
VTF_TO_DXGI_LINEAR = {
    13: 71, 20: 71, 14: 74, 15: 77, 37: 83, 38: 80,
}


def mip_size(w, h, fmt):
    bpp, block, side = VTF_FMT_INFO[fmt]
    if block:
        bw = max(1, (w + side - 1) // side)
        bh = max(1, (h + side - 1) // side)
        return bw * bh * block
    return w * h * bpp


class VTF:
    def __init__(self):
        self.major = self.minor = 0
        self.header_size = 0
        self.width = self.height = 0
        self.flags = 0
        self.frames = self.first_frame = 0
        self.bump_scale = 1.0
        self.hi_format = 0
        self.lo_format = 0
        self.mip_count = 0
        self.lo_width = self.lo_height = 0
        self.depth = 1
        self.num_resources = 0
        self.mips = []          # mip0 .. mipN (bytes)
        self.lowres = b''

    @property
    def format_name(self):
        return VTF_FORMATS.get(self.hi_format, 'FMT%d' % self.hi_format)

    @staticmethod
    def read(data):
        v = VTF()
        sig, v.major, v.minor, v.header_size = struct.unpack_from('<4sIII', data, 0)
        if sig != b'VTF\0':
            raise ValueError('not a VTF')
        v.width, v.height = struct.unpack_from('<HH', data, 16)
        v.flags, = struct.unpack_from('<I', data, 20)
        v.frames, v.first_frame = struct.unpack_from('<HH', data, 24)
        v.bump_scale, = struct.unpack_from('<f', data, 48)
        v.hi_format, = struct.unpack_from('<I', data, 52)
        v.mip_count = data[56]
        v.lo_format = data[57]
        v.lo_width, v.lo_height = data[61], data[62]
        v.depth, = struct.unpack_from('<H', data, 63)
        if v.depth == 0:
            v.depth = 1
        if v.frames == 0:
            v.frames = 1
        if v.mip_count == 0:
            v.mip_count = 1

        off = v.header_size
        for m in range(v.mip_count):
            w = max(1, v.width >> m)
            h = max(1, v.height >> m)
            sz = mip_size(w, h, v.hi_format) * v.frames * v.depth
            if off + sz > len(data):
                raise ValueError('VTF mip %d runs past EOF (%d+%d > %d)'
                                 % (m, off, sz, len(data)))
            v.mips.append(data[off:off + sz])
            off += sz
        if v.lo_width and v.lo_height:
            sz = mip_size(v.lo_width, v.lo_height, v.lo_format)
            v.lowres = data[off:off + sz]
            off += sz
        v.trailing = len(data) - off
        return v

    def mip0_size(self):
        return mip_size(self.width, self.height, self.hi_format)

    def summary(self):
        return ('%dx%d %s mips=%d frames=%d lo=%dx%d/%s trailing=%d'
                % (self.width, self.height, self.format_name, self.mip_count,
                   self.frames, self.lo_width, self.lo_height,
                   VTF_FORMATS.get(self.lo_format, str(self.lo_format)),
                   getattr(self, 'trailing', 0)))


# ------------------------------------------------------------------ DDS build
def build_dds_dx10(width, height, dxgi_format, mips, array_size=1,
                   srgb_misc=0, ref_header=None):
    """Wrap a BC-compressed mip chain in a DX10 DDS.

    `ref_header` (148 bytes) is an optional real HD2 DDS header; when given we
    copy every flag/caps byte from it and only overwrite the dimensions, the
    mip count, the DXGI format and the array size -- that is byte-for-byte the
    shape a shipping patch uses.
    """
    hdr = bytearray(ref_header) if ref_header else bytearray(148)
    if ref_header is None:
        struct.pack_into('<4s', hdr, 0, b'DDS ')
        struct.pack_into('<I', hdr, 4, 124)
        struct.pack_into('<I', hdr, 8, 0x000a1007)
        struct.pack_into('<I', hdr, 24, 0)
        struct.pack_into('<I', hdr, 76, 32)
        struct.pack_into('<I', hdr, 80, 0x4)
        struct.pack_into('<4s', hdr, 84, b'DX10')
        struct.pack_into('<I', hdr, 108, 0x1000)
        struct.pack_into('<I', hdr, 132, 3)
        struct.pack_into('<I', hdr, 136, 0)
        struct.pack_into('<I', hdr, 144, 0)
    struct.pack_into('<I', hdr, 12, height)
    struct.pack_into('<I', hdr, 16, width)
    struct.pack_into('<I', hdr, 20, width)          # same convention as shipping patches
    struct.pack_into('<I', hdr, 28, len(mips))
    struct.pack_into('<I', hdr, 128, dxgi_format)
    struct.pack_into('<I', hdr, 140, array_size)
    return bytes(hdr) + b''.join(mips)
