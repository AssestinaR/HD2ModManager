# -*- coding: utf-8 -*-
"""L4D2 VTF -> HD2 `Texture` asset.

An HD2 texture entry is 340 bytes of TocData (a 12-byte id, 15 zeroed
`StingrayMipmapInfo` slots and a 148-byte **DX10** DDS header) plus the raw
pixel data in the GPU sidecar.  The shipping Chiffon patch ships exactly
`mipmap_info == 0` and `mipCount == 1` for all eight of its textures, i.e.
**mip 0 only**, so we do the same.

Everything is re-encoded to BC7, which is the only format the one working
reference patch we have actually uses:

    base colour  -> BC7_UNORM_SRGB (99)
    normal / PBR -> BC7_UNORM      (98)

The DDS wrapper is fed to the SDK's own `deps/texconv.exe`, so the encode is
byte-for-byte the same tool the modding community uses.
"""
import os
import struct
import subprocess
import sys
import tempfile

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_asset as A
import hd2_vtf as V

TEXCONV = ('E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0/'
           'deps/texconv.exe')

DXGI_BC7_SRGB = 99
DXGI_BC7_UNORM = 98

# VTF high-res format -> the DXGI format used for the *wrapper* DDS we hand to
# texconv.  Linear variants: we re-encode anyway, so the wrapper only has to be
# *decodable*, and texconv applies the right transfer function for the target.
VTF_TO_WRAPPER_DXGI = {
    13: 71,     # DXT1        -> BC1_UNORM
    20: 71,     # DXT1 1-bit  -> BC1_UNORM
    14: 74,     # DXT3        -> BC2_UNORM
    15: 77,     # DXT5        -> BC3_UNORM
    37: 83,     # BC5/ATI2N   -> BC5_UNORM
    38: 80,     # BC4/ATI1N   -> BC4_UNORM
}
# uncompressed VTF formats -> how to unpack the bytes (see VTF_FORMATS)
VTF_UNCOMPRESSED = {
    0: 'RGBA',      # RGBA8888
    2: 'RGB',       # RGB888
    3: 'BGR',       # BGR888
    12: 'BGRA',     # BGRA8888
    16: 'BGRX',     # BGRX8888
    5: 'L',         # I8
    6: 'LA',        # IA88
    8: 'L',         # A8
}

REF_DDS_HEADER = None       # filled by `reference_header()`


def reference_header(path):
    """Grab a real 148-byte DX10 DDS header out of a shipping patch."""
    global REF_DDS_HEADER
    import hd2_patch as HP
    entries, _raw = HP.scan_entries(path)
    for e in entries:
        if e['type_id'] != A.TEXTURE_TYPE_ID:
            continue
        t = A.Texture.read(HP.entry_payload(e, path)['toc'])
        REF_DDS_HEADER = bytes(t.dds_header)
        return REF_DDS_HEADER
    raise ValueError('no texture entry in %s' % path)


def _run_texconv(src, outdir, dxgi, width, height):
    """-> the 148-byte DX10 header + mip0 pixels, as texconv wrote them."""
    name = ('BC7_UNORM_SRGB' if dxgi == DXGI_BC7_SRGB else 'BC7_UNORM')
    cmd = [TEXCONV, '-nologo', '-y', '-f', name, '-m', '1', '-o', outdir, src]
    p = subprocess.run(cmd, capture_output=True)
    if p.returncode != 0:
        raise RuntimeError('texconv failed (%d): %s\n%s'
                           % (p.returncode,
                              p.stdout.decode('utf-8', 'replace'),
                              p.stderr.decode('utf-8', 'replace')))
    out = os.path.join(outdir, os.path.splitext(os.path.basename(src))[0] + '.dds')
    if not os.path.exists(out):
        raise RuntimeError('texconv produced no output for %s\n%s' % (src, p.stdout.decode('utf-8','replace')))
    data = open(out, 'rb').read()
    if data[84:88] != b'DX10':
        raise RuntimeError('texconv did not emit a DX10 header')
    return bytearray(data[:A.DDS_HEADER_SIZE]), data[A.DDS_HEADER_SIZE:]


def _wrapper_dds(vtf, dxgi, w, h, pixels, ref):
    hdr = bytearray(ref) if ref else bytearray(A.DDS_HEADER_SIZE)
    struct.pack_into('<4s', hdr, 0, b'DDS ')
    struct.pack_into('<I', hdr, 4, 124)
    struct.pack_into('<I', hdr, 8, 0x000a1007)
    struct.pack_into('<I', hdr, 12, h)
    struct.pack_into('<I', hdr, 16, w)
    struct.pack_into('<I', hdr, 20, w)
    struct.pack_into('<I', hdr, 24, 0)
    struct.pack_into('<I', hdr, 28, 1)
    struct.pack_into('<I', hdr, 76, 32)
    struct.pack_into('<I', hdr, 80, 0x4)
    struct.pack_into('<4s', hdr, 84, b'DX10')
    struct.pack_into('<I', hdr, 108, 0x1000)
    struct.pack_into('<I', hdr, 128, dxgi)
    struct.pack_into('<I', hdr, 132, 3)
    struct.pack_into('<I', hdr, 136, 0)
    struct.pack_into('<I', hdr, 140, 1)
    struct.pack_into('<I', hdr, 144, 0)
    return hdr + pixels


def _uncompressed_png(vtf, tmpdir, tag):
    from PIL import Image
    fmt = vtf.hi_format
    if fmt not in VTF_UNCOMPRESSED:
        raise ValueError('VTF format %d (%s) is neither BC nor a known raw layout'
                         % (fmt, V.VTF_FORMATS.get(fmt, '?')))
    mode = VTF_UNCOMPRESSED[fmt]
    w, h = vtf.width, vtf.height
    mip0 = vtf.mips[0]
    need = w * h * len(mode)
    if len(mip0) < need:
        raise ValueError('VTF mip0 too short for %dx%d %s' % (w, h, mode))
    arr = np.frombuffer(mip0[:need], np.uint8).reshape(h, w, len(mode))
    if mode == 'RGB':
        img = Image.fromarray(arr, 'RGB')
    elif mode == 'BGR':
        img = Image.fromarray(arr[:, :, ::-1], 'RGB')
    elif mode == 'RGBA':
        img = Image.fromarray(arr, 'RGBA')
    elif mode == 'BGRA':
        img = Image.fromarray(arr[:, :, [2, 1, 0, 3]], 'RGBA')
    elif mode == 'BGRX':
        img = Image.fromarray(arr[:, :, [2, 1, 0]], 'RGB')
    elif mode == 'L':
        img = Image.fromarray(arr[:, :, 0], 'L')
    elif mode == 'LA':
        img = Image.fromarray(arr, 'LA')
    else:
        raise ValueError('unhandled mode %s' % mode)
    p = os.path.join(tmpdir, tag + '.png')
    img.save(p)
    return p


def vtf_to_dds(vtf_bytes, dxgi, ref=None, tmpdir=None, tag='tex'):
    """-> 148-byte DX10 header + mip0 pixels, re-encoded to `dxgi`."""
    v = V.VTF.read(vtf_bytes)
    own = tmpdir is None
    tmpdir = tmpdir or tempfile.mkdtemp(prefix='texpack')
    try:
        if v.hi_format in VTF_TO_WRAPPER_DXGI:
            src = os.path.join(tmpdir, tag + '.dds')
            with open(src, 'wb') as f:
                f.write(_wrapper_dds(v, VTF_TO_WRAPPER_DXGI[v.hi_format],
                                     v.width, v.height, v.mips[0], ref))
        else:
            src = _uncompressed_png(v, tmpdir, tag)
        hdr, px = _run_texconv(src, tmpdir, dxgi, v.width, v.height)
    finally:
        if own:
            import shutil
            shutil.rmtree(tmpdir, ignore_errors=True)
    # normalise the header to exactly the shipping shape
    struct.pack_into('<I', hdr, 12, v.height)
    struct.pack_into('<I', hdr, 16, v.width)
    struct.pack_into('<I', hdr, 20, v.width)
    struct.pack_into('<I', hdr, 28, 1)
    struct.pack_into('<I', hdr, 128, dxgi)
    struct.pack_into('<I', hdr, 140, 1)
    return bytes(hdr), px


def build_texture(vtf_bytes, dxgi, file_id, ref=None, tmpdir=None, tag='tex'):
    """-> (Texture asset, gpu pixel bytes).

    `file_id` is the patch entry id, NOT the asset's `UnkID`: every texture in
    the shipping reference patch carries `UnkID == 0`, so we do too.
    """
    hdr, px = vtf_to_dds(vtf_bytes, dxgi, ref=ref, tmpdir=tmpdir, tag=tag)
    t = A.Texture()
    t.unk_id = 0
    t.dds_header = bytearray(hdr)
    t.pixels = px
    return t, px


def solid_texture(width, height, rgba, dxgi=DXGI_BC7_UNORM, ref=None, tmpdir=None):
    """A flat-colour texture (used for the synthetic PBR slot)."""
    from PIL import Image
    own = tmpdir is None
    tmpdir = tmpdir or tempfile.mkdtemp(prefix='texpack')
    try:
        p = os.path.join(tmpdir, 'solid.png')
        Image.fromarray(np.tile(np.asarray(rgba, np.uint8), (height, width, 1)),
                        'RGBA').save(p)
        hdr, px = _run_texconv(p, tmpdir, dxgi, width, height)
    finally:
        if own:
            import shutil
            shutil.rmtree(tmpdir, ignore_errors=True)
    struct.pack_into('<I', hdr, 12, height)
    struct.pack_into('<I', hdr, 16, width)
    struct.pack_into('<I', hdr, 20, width)
    struct.pack_into('<I', hdr, 28, 1)
    struct.pack_into('<I', hdr, 128, dxgi)
    struct.pack_into('<I', hdr, 140, 1)
    return bytes(hdr), px


if __name__ == '__main__':
    import hd2_source as S
    packs = S.Packs()
    ref = reference_header(
        r'E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310\9ba626afa44a3aa3.patch_0')
    print('reference DDS header: %d bytes, dxgi=%d'
          % (len(ref), struct.unpack_from('<I', ref, 128)[0]))
    d = tempfile.mkdtemp(prefix='texpack-demo')
    for nm, dxgi in (('shinano_body', DXGI_BC7_SRGB),
                     ('white_n', DXGI_BC7_UNORM),
                     ('ornament_metal_underwear', DXGI_BC7_SRGB),
                     ('skinwarp', DXGI_BC7_SRGB)):
        raw = packs.vtf_bytes(nm)
        if raw is None:
            print('  %-28s NOT FOUND' % nm)
            continue
        hdr, px = vtf_to_dds(raw, dxgi, ref=ref, tmpdir=d, tag=nm)
        v = V.VTF.read(raw)
        print('  %-28s %s %dx%d mips=%d -> dxgi=%d header=%d px=%d'
              % (nm, v.format_name, v.width, v.height, v.mip_count,
                 struct.unpack_from('<I', hdr, 128)[0], len(hdr), len(px)))
    import shutil
    shutil.rmtree(d, ignore_errors=True)
