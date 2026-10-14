# -*- coding: utf-8 -*-
"""Round 71 experiment: darken the ALBEDO textures of a shipping patch.

Why: L4D2's NekoToon multiplies albedo by a <=0.30 lightwarp; HD2's `advanced`
PBR has no such ceiling, so the same texel renders much brighter ("washed out",
skin especially white).  Multiplying the albedo down is the most direct way to
get the L4D2 look back.

Scope (deliberately narrow, so it is trivially revertible):
    * ONLY textures used as ALBEDO (advanced slot 5 / alphaclip slot 2)
    * ONLY the big ones (>= --min-size, default 64) -- 1x1 placeholders are
      matcap-driven and must NOT be darkened
    * ONLY the pixel payload -- DDS header, TocData, every other entry, the
      patch layout and the whole GPU sidecar structure stay byte-identical
      except for the re-encoded albedo pixel blocks

Two ways to darken (pick with --mode):
    mul   : plain multiply, new = rgb * factor          (uniform dimming)
    gamma : new = 255 * (rgb/255) ** gamma              (dims midtones, keeps
                                                         highlights/black)

The mask (alphaclip slot 1) is left alone.  Normal / metallic / roughness are
linear PBR inputs: darkening those would corrupt the shading, so they are
skipped.

usage:
  darken_albedo.py --src <patch_0> --out <dir> --name <n> --factor 0.4
                   [--mode mul|gamma] [--min-size 64] [--only a,b,name]
                   [--src-gpu <gpu_resources> if not next to the patch]
"""
import argparse
import hashlib
import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_asset as A
import hd2_patch as HP
import hd2_texpack as TP

TEXCONV = TP.TEXCONV
DXGI_BC7_SRGB = TP.DXGI_BC7_SRGB


def log(m=''):
    print(m, flush=True)


def _run(cmd):
    p = subprocess.run(cmd, capture_output=True)
    if p.returncode != 0:
        raise RuntimeError('cmd failed: %s\n%s\n%s'
                           % (cmd, p.stdout.decode('utf-8', 'replace'),
                              p.stderr.decode('utf-8', 'replace')))
    return p


def decode_texture(t, tmpdir, tag):
    """Texture -> RGBA uint8 array (sRGB decode)."""
    from PIL import Image
    dds = os.path.join(tmpdir, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(t.to_dds())
    png = os.path.join(tmpdir, tag + '.png')
    if os.path.exists(png):
        os.remove(png)
    _run([TEXCONV, '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM_SRGB',
          '-m', '1', '-o', tmpdir, dds])
    if not os.path.exists(png):
        raise RuntimeError('decode failed for %s' % tag)
    return np.asarray(Image.open(png).convert('RGBA'), np.uint8)


def encode_srgb(rgba, tmpdir, tag):
    """RGBA uint8 -> BC7_UNORM_SRGB pixel bytes (mip0 only)."""
    from PIL import Image
    png = os.path.join(tmpdir, tag + '_out.png')
    Image.fromarray(rgba, 'RGBA').save(png)
    hdr, px = TP._run_texconv(png, tmpdir, DXGI_BC7_SRGB, rgba.shape[1], rgba.shape[0])
    return px


def _lum(rgba):
    a = rgba[:, :, :3].astype(np.float64)
    return float((0.2126 * a[:, :, 0] + 0.7152 * a[:, :, 1] + 0.0722 * a[:, :, 2]).mean())


def darken(rgba, factor, mode):
    rgb = rgba[:, :, :3].astype(np.float32)
    if mode == 'mul':
        out = rgb * factor
    elif mode == 'gamma':
        out = 255.0 * np.power(np.clip(rgb, 0, 255) / 255.0, factor)
    elif mode == 'compress':
        # What L4D2's lightwarp actually does: a soft ceiling on the LIT part
        # only.  Dark texels are barely touched, bright texels are pulled down
        # towards `factor`*255.  Reinhard-style: out = x*(1+a) / (1 + x*a/ceil)
        # with `factor` = the ceiling as a fraction (e.g. 0.45).
        ceil = 255.0 * factor
        a = 2.0
        x = np.clip(rgb, 0, 255)
        out = x * (1.0 + a) / (1.0 + a * x / max(ceil, 1.0))
    else:
        raise ValueError('unknown mode %r' % mode)
    out = np.clip(np.round(out), 0, 255).astype(np.uint8)
    return np.dstack([out, rgba[:, :, 3]])


def albedo_ids(patch, min_size):
    """-> {tex_id: (w, h, nslots)} for textures used as albedo and big enough."""
    entries, _raw = HP.scan_entries(patch)
    tex = {}
    for e in entries:
        if e['type_id'] != A.TEXTURE_TYPE_ID:
            continue
        t = A.Texture.read(HP.entry_payload(e, patch)['toc'],
                           HP.entry_payload(e, patch)['gpu'])
        hdr = bytes(t.dds_header)
        tex[e['file_id']] = (struct.unpack_from('<I', hdr, 16)[0],
                             struct.unpack_from('<I', hdr, 12)[0])
    out = {}
    for e in entries:
        if e['type_id'] != A.MATERIAL_TYPE_ID:
            continue
        m = A.Material.read(HP.entry_payload(e, patch)['toc'])
        n = len(m.tex_ids)
        idx = 5 if n == 11 else (2 if n == 3 else None)
        if idx is None or idx >= n:
            continue
        tid = m.tex_ids[idx]
        if tid not in tex:
            continue
        w, h = tex[tid]
        if w < min_size or h < min_size:
            continue
        out[tid] = (w, h)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--name', default='lilium-dark')
    ap.add_argument('--factor', type=float, required=True)
    ap.add_argument('--mode', choices=['mul', 'gamma', 'compress'], default='mul')
    ap.add_argument('--min-size', type=int, default=64)
    ap.add_argument('--only', default='',
                    help='comma list of tex ids (hex) to limit the pass to')
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    src = args.src
    gpu_path = src + '.gpu_resources'
    if not os.path.exists(gpu_path):
        raise SystemExit('missing %s' % gpu_path)

    raw = open(src, 'rb').read()
    gpu = open(gpu_path, 'rb').read()

    ids = albedo_ids(src, args.min_size)
    if args.only:
        want = {int(x, 16) for x in args.only.split(',') if x.strip()}
        ids = {k: v for k, v in ids.items() if k in want}

    log('%s: %d albedo textures, mode=%s factor=%.3f'
        % (args.name, len(ids), args.mode, args.factor))
    for tid, (w, h) in sorted(ids.items()):
        log('  albedo 0x%016x %4dx%-5d' % (tid, w, h))

    if args.dry_run:
        log('dry run: nothing written')
        return 0

    # ---- rewrite the GPU sidecar -----------------------------------------
    # Every texture's pixel payload lives at a known offset in the sidecar;
    # we rebuild the sidecar byte-for-byte, substituting only the albedo
    # pixel blocks.  Offsets come from the patch's own chunk table.
    entries, _raw2 = HP.scan_entries(src)
    tmp = tempfile.mkdtemp(prefix='darken')
    patched = bytearray(gpu)
    log('')

    for e in entries:
        if e['type_id'] != A.TEXTURE_TYPE_ID or e['file_id'] not in ids:
            continue
        pl = HP.entry_payload(e, src)
        t = A.Texture.read(pl['toc'], pl['gpu'])
        w, h = ids[e['file_id']]
        rgba = decode_texture(t, tmp, 'alb_%016x' % e['file_id'])
        if rgba.shape[0] != h or rgba.shape[1] != w:
            log('  [warn] 0x%016x decoded %s != %dx%d; skipping'
                % (e['file_id'], rgba.shape[:2], h, w))
            continue
        new = darken(rgba, args.factor, args.mode)
        px = encode_srgb(new, tmp, 'alb_%016x' % e['file_id'])
        if len(px) != len(t.pixels):
            raise RuntimeError('0x%016x re-encode changed size %d -> %d'
                               % (e['file_id'], len(t.pixels), len(px)))
        # where do this texture's pixels live in the GPU sidecar?
        # Texture.read(toc, gpu) takes the WHOLE gpu chunk as `pixels`, so the
        # pixel block starts exactly at the entry's own gpu_offset.
        start = e['gpu_offset']
        if start is None or start + len(px) > len(patched):
            raise RuntimeError('0x%016x: GPU pixel range %s+%d out of %d'
                               % (e['file_id'], start, len(px), len(patched)))
        patched[start:start + len(px)] = px
        before = _lum(rgba)
        after = _lum(new)
        log('  0x%016x  luminance %6.1f -> %6.1f  (%+.1f)'
            % (e['file_id'], before, after, after - before))

    # ---- write out --------------------------------------------------------
    # The patch container is untouched (textures' TocData only stores the
    # header, not the pixels), so we copy it verbatim.
    os.makedirs(args.out, exist_ok=True)
    shutil.copyfile(src, os.path.join(args.out, os.path.basename(src)))
    open(os.path.join(args.out, os.path.basename(src) + '.gpu_resources'), 'wb').write(bytes(patched))

    sha_p = hashlib.sha256(open(os.path.join(args.out, os.path.basename(src)), 'rb').read()).hexdigest()
    sha_g = hashlib.sha256(bytes(patched)).hexdigest()
    log('')
    log('out %s' % args.out)
    log('  patch sha256 %s' % sha_p)
    log('  gpu   sha256 %s' % sha_g)

    report = dict(schema='hd2mod.rig-lab-darken', source=src, out=args.out,
                  mode=args.mode, factor=args.factor, minSize=args.min_size,
                  textures=[dict(id='0x%016x' % k, w=v[0], h=v[1])
                            for k, v in sorted(ids.items())],
                  patchSha256=sha_p, gpuSha256=sha_g)
    open(os.path.join(args.out, args.name + '.manifest.json'), 'w', encoding='utf-8').write(
        json.dumps(report, indent=1, ensure_ascii=False))
    return 0


if __name__ == '__main__':
    sys.exit(main())
