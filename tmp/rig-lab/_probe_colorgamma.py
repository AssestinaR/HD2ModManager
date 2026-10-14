# -*- coding: utf-8 -*-
"""Is the L4D2 -> HD2 "washed out / grey-white" look OUR bug or HD2's style?

For the same source VTF we produce three numbers:

  (a) source-sRGB   : source VTF wrapper tagged _SRGB, decoded sRGB in/sRGB out
                      -> what the artist authored
  (b) source-linear : source VTF wrapper tagged _UNORM (what fix_appearance
                      actually feeds texconv), decoded straight to UNORM
  (c) shipped       : the texture that ends up in our HD2 patch slot 5 (albedo)

If (b) is much brighter than (a) -> our decode path has a gamma bug baked in.
If (c) tracks (a) but the GAME still looks grey -> it's HD2's own colour style.

usage: _probe_colorgamma.py [patch_0] [stem ...]
"""
import os, sys, struct, tempfile, subprocess
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_asset as A
import hd2_patch as HP
import hd2_vtf as V
import hd2_texpack as TP
import hd2_source as S
from PIL import Image

TEXCONV = TP.TEXCONV

# VTF -> DXGI, the *sRGB* variants (what the source artists authored in)
VTF_TO_WRAPPER_SRGB = {
    13: 72,     # DXT1  -> BC1_UNORM_SRGB
    20: 72,
    14: 75,     # DXT3  -> BC2_UNORM_SRGB
    15: 78,     # DXT5  -> BC3_UNORM_SRGB
}


def _run(cmd):
    p = subprocess.run(cmd, capture_output=True)
    if p.returncode != 0:
        raise RuntimeError('cmd failed: %s\n%s\n%s'
                           % (cmd, p.stdout.decode('utf-8', 'replace'),
                              p.stderr.decode('utf-8', 'replace')))
    return p


def _wrap(v, dxgi):
    return TP._wrapper_dds(v, dxgi, v.width, v.height, v.mips[0], None)


def _decode_dds(dds_path, tmpdir, srgb):
    """texconv writes <inputstem>.png next to, or into -o dir."""
    stem = os.path.splitext(os.path.basename(dds_path))[0]
    png = os.path.join(tmpdir, stem + '.png')
    if os.path.exists(png):
        os.remove(png)
    fmt = 'R8G8B8A8_UNORM_SRGB' if srgb else 'R8G8B8A8_UNORM'
    _run([TEXCONV, '-nologo', '-y', '-ft', 'png', '-f', fmt, '-m', '1',
          '-o', tmpdir, dds_path])
    if not os.path.exists(png):
        return None
    return np.asarray(Image.open(png).convert('RGB'), np.float64)


def stats(a, label):
    if a is None:
        print('  %-20s <none>' % label)
        return
    print('  %-20s mean %6.1f %6.1f %6.1f | min %3d %3d %3d | max %3d %3d %3d | Lmean %6.1f'
          % (label, a[:, :, 0].mean(), a[:, :, 1].mean(), a[:, :, 2].mean(),
             a[:, :, 0].min(), a[:, :, 1].min(), a[:, :, 2].min(),
             a[:, :, 0].max(), a[:, :, 1].max(), a[:, :, 2].max(), a.mean()))


def main():
    patch = sys.argv[1] if len(sys.argv) > 1 else \
        r'E:/mod/Lilium-Sancta-RE2310/9ba626afa44a3aa3.patch_0'
    stems = sys.argv[2:]

    tmp = tempfile.mkdtemp(prefix='colgamma')
    packs = S.Packs()
    print('source packs: %d VMT / %d VTF' % (len(packs.vmt), len(packs.vtf)))

    entries, _raw = HP.scan_entries(patch)
    tex = {e['file_id']: e for e in entries if e['type_id'] == A.TEXTURE_TYPE_ID}
    print('patch textures: %d\n' % len(tex))

    if not stems:
        stems = sorted({os.path.basename(v)[:-4] for v in packs.vtf
                        if os.path.basename(v).endswith('.vtf')})

    for stem in stems:
        raw = packs.vtf_bytes(stem)
        if raw is None:
            continue
        v = V.VTF.read(raw)
        if v.hi_format not in TP.VTF_TO_WRAPPER_DXGI:
            continue        # only BC formats have a faithful wrapper path
        print('%-22s VTF %dx%d %s' % (stem, v.width, v.height, v.format_name))

        # (a) correct sRGB decode
        dds_a = os.path.join(tmp, stem + '_s.dds')
        with open(dds_a, 'wb') as f:
            f.write(_wrap(v, VTF_TO_WRAPPER_SRGB.get(
                v.hi_format, TP.VTF_TO_WRAPPER_DXGI.get(v.hi_format, 71))))
        a = _decode_dds(dds_a, tmp, srgb=True)

        # (b) what our pipeline actually does
        dds_b = os.path.join(tmp, stem + '_l.dds')
        with open(dds_b, 'wb') as f:
            f.write(_wrap(v, TP.VTF_TO_WRAPPER_DXGI[v.hi_format]))
        b = _decode_dds(dds_b, tmp, srgb=False)

        stats(a, '(a) source-sRGB')
        stats(b, '(b) pipeline')
        if a is not None and b is not None:
            r = b.mean() / max(a.mean(), 1e-6)
            print('  %-20s (b)/(a) L ratio = %.3f   (%+.0f%%)'
                  % ('', r, 100.0 * (r - 1.0)))
        print()


if __name__ == '__main__':
    main()
