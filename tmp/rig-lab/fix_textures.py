# -*- coding: utf-8 -*-
"""M6: repack the 14 base-colour textures with the fixed VTF reader.

Why
---
`hd2_vtf.VTF.read` used to slice the mip chain largest-first, so every shipped
base texture was actually the "mip pyramid" reinterpretation of the image block
(see hd2_vtf's docstring).  That is the picture the game showed.  The fix reads
mip0 from the tail of the block.  Nothing else changes.

What this script does
---------------------
Walks the candidate patch, and for every base-colour texture entry it

  1. *proves* it understands the current bytes: it re-runs the OLD pipeline
     (largest-first slice -> BC7 -> alpha-zero) and asserts the result equals
     the bytes already in the patch.  If this fails we are looking at the wrong
     source VTF or the wrong recipe, and the script stops.
  2. rebuilds the entry with the SAME recipe but the FIXED mip0.
  3. asserts the DDS header (hence the whole 340-byte texture TOC) is
     byte-identical, and the GPU payload keeps its exact length.

Every other entry is carried over verbatim, and the container checks from
`fix_materials.py` are repeated: entry count, verbatim bytes, GPU offsets, blob
sizes.

usage:
  python fix_textures.py --src E:/tmp/rig-lab/cand-m5/9ba626afa44a3aa3.patch_0 \
      --manifest E:/tmp/rig-lab/native-re2310/lunalice-native-re2310.manifest.json \
      --out E:/tmp/rig-lab/cand-m6 --name lunalice-m6
"""
import argparse
import json
import os
import shutil
import struct
import sys
import tempfile

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_asset as A
import hd2_patch as HP
import hd2_patchw as PW
import hd2_source as S
import hd2_texpack as TP
import hd2_vtf as V
import fix_materials as FM

PATCH_NAME = '9ba626afa44a3aa3.patch_0'
DXGI_SRGB = TP.DXGI_BC7_SRGB


def log(m=''):
    print(m, flush=True)


def _uncompressed_png(v, payload, tmpdir, tag):
    """Same unpacking as hd2_texpack._uncompressed_png, but for a given payload."""
    from PIL import Image
    fmt = v.hi_format
    if fmt not in TP.VTF_UNCOMPRESSED:
        raise ValueError('VTF format %d is neither BC nor a known raw layout' % fmt)
    mode = TP.VTF_UNCOMPRESSED[fmt]
    w, h = v.width, v.height
    need = w * h * len(mode)
    if len(payload) < need:
        raise ValueError('payload too short for %dx%d %s' % (w, h, mode))
    arr = np.frombuffer(payload[:need], np.uint8).reshape(h, w, len(mode))
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


def gpu_from_payload(v, payload, dxgi, tmpdir, tag):
    """BC7-encode one VTF mip0 payload exactly the way hd2_texpack does."""
    if v.hi_format in TP.VTF_TO_WRAPPER_DXGI:
        src = os.path.join(tmpdir, tag + '.dds')
        with open(src, 'wb') as f:
            f.write(V.build_dds_dx10(v.width, v.height,
                                     TP.VTF_TO_WRAPPER_DXGI[v.hi_format], [payload]))
    else:
        src = _uncompressed_png(v, payload, tmpdir, tag)
    hdr, px = TP._run_texconv(src, tmpdir, dxgi, v.width, v.height)
    return bytes(hdr), px


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--manifest', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--name', default='lunalice-m6')
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    manifest = json.load(open(args.manifest, encoding='utf-8'))
    bases = {}
    for t in manifest['textures']:
        if t['name'].startswith('base/'):
            bases[int(t['id'], 16)] = t['name'][len('base/'):]
    log('=' * 78)
    log('fix_textures -- %s' % args.src)
    log('  %d base textures declared by the manifest' % len(bases))

    entries, _raw = HP.scan_entries(args.src)
    log('  source: %d entries, %d bytes' % (len(entries), os.path.getsize(args.src)))

    packs = S.Packs()
    tmpdir = tempfile.mkdtemp(prefix='texfix')
    out_entries, replaced = [], set()
    report = []
    try:
        for e in entries:
            pl = HP.entry_payload(e, args.src)
            stem = bases.get(e['file_id'])
            if e['type_id'] != A.TEXTURE_TYPE_ID or stem is None:
                out_entries.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'],
                                            pl['stream'], pl['gpu'],
                                            e['unknown1'], e['unknown2'],
                                            note='verbatim'))
                continue

            raw = packs.vtf_bytes(stem)
            if raw is None:
                raise RuntimeError('no source VTF for base texture %r' % stem)
            v = V.VTF.read(raw)
            hdr_off = struct.unpack_from('<I', raw, 12)[0]
            block = raw[hdr_off:]
            m0 = V.mip_size(v.width, v.height, v.hi_format) * v.frames * v.depth

            # ---- 1. reproduce the OLD bytes ---------------------------------
            hdr_o, px_o = gpu_from_payload(v, block[:m0], DXGI_SRGB, tmpdir, stem + '_old')
            px_o = FM.zero_alpha(hdr_o, px_o, tmpdir, stem + '_old')
            same = (px_o == bytes(pl['gpu']))
            if not same:
                raise AssertionError(
                    'cannot reproduce the shipped pixels of %s (0x%016x); '
                    'the old-order payload does not round-trip'
                    % (stem, e['file_id']))

            # ---- 2. rebuild with the fixed mip0 -----------------------------
            t_n, px_n = TP.build_texture(raw, DXGI_SRGB, e['file_id'],
                                         ref=None, tmpdir=tmpdir, tag=stem + '_new')
            hdr_n = bytes(t_n.dds_header)
            px_n = FM.zero_alpha(hdr_n, px_n, tmpdir, stem + '_new')

            # ---- 3. shape must not move -------------------------------------
            t_old = A.Texture.read(pl['toc'], pl['gpu'])
            t_new = A.Texture()
            t_new.unk_id = t_old.unk_id
            t_new.dds_header = bytearray(hdr_n)
            t_new.pixels = px_n
            if bytes(t_new.dds_header) != bytes(t_old.dds_header):
                raise AssertionError('DDS header changed for %s' % stem)
            if t_new.write_toc() != pl['toc']:
                raise AssertionError('texture TOC changed for %s' % stem)
            if len(px_n) != len(pl['gpu']):
                raise AssertionError('gpu size changed for %s (%d -> %d)'
                                     % (stem, len(pl['gpu']), len(px_n)))

            diff = sum(1 for a, b in zip(px_o, px_n) if a != b)
            report.append(dict(name=stem, id='%016x' % e['file_id'],
                               size='%dx%d' % (v.width, v.height),
                               fmt=v.format_name,
                               gpuBytes=len(px_n),
                               changedBytes=diff,
                               changedPct=100.0 * diff / max(1, len(px_n))))
            out_entries.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'],
                                        pl['stream'], px_n,
                                        e['unknown1'], e['unknown2'],
                                        note='texture:base-fixed'))
            replaced.add(e['index'])
    finally:
        shutil.rmtree(tmpdir, ignore_errors=True)

    log('  %d base textures rebuilt, %d entries verbatim'
        % (len(replaced), len(out_entries) - len(replaced)))
    log('  %-34s %-11s %-8s %-9s %s' % ('texture', 'size', 'format', 'gpu B', 'changed'))
    for r in report:
        log('  %-34s %-11s %-8s %-9d %d (%.1f%%)'
            % (r['name'], r['size'], r['fmt'], r['gpuBytes'],
               r['changedBytes'], r['changedPct']))

    if args.dry_run:
        log('  dry run: not writing')
        return 0

    res = PW.write_patch(out_entries, args.src, args.out, PATCH_NAME)
    log('  wrote %s (%d B) + gpu (%d B)'
        % (res['toc'], res['toc_size'], res['gpu_size']))

    # ---- container sanity, same contract as fix_materials ----------------
    new_entries, _ = HP.scan_entries(res['toc'])
    assert len(new_entries) == len(entries), 'entry count changed'
    for a, b in zip(entries, new_entries):
        if a['index'] in replaced:
            assert a['type_id'] == b['type_id'], 'type changed at %d' % a['index']
            continue
        if (a['file_id'], a['type_id']) != (b['file_id'], b['type_id']):
            raise AssertionError('entry %d identity changed' % a['index'])
        assert HP.entry_payload(a, args.src) == HP.entry_payload(b, res['toc']), \
            'verbatim entry %d (0x%016x) changed' % (a['index'], a['file_id'])
        assert a['gpu_offset'] == b['gpu_offset'], \
            'gpu offset of verbatim entry %d moved' % a['index']
    assert res['toc_size'] == os.path.getsize(args.src), \
        'TOC size changed (%d vs %d)' % (res['toc_size'], os.path.getsize(args.src))
    assert res['gpu_size'] == os.path.getsize(args.src + '.gpu_resources'), \
        'gpu blob size changed'

    meta = dict(schema='hd2mod.rig-lab-texturefix',
                sourcePatch=args.src, sourceManifest=args.manifest,
                rootCause='hd2_vtf.VTF.read sliced the mip chain largest-first; '
                          'the real mip0 is the tail of the image block',
                replacedTextures=len(replaced),
                textures=report,
                outputPatch=res['toc'], tocBytes=res['toc_size'],
                gpuBytes=res['gpu_size'], entryCount=len(out_entries))
    os.makedirs(args.out, exist_ok=True)
    mpath = os.path.join(args.out, args.name + '.manifest.json')
    with open(mpath, 'w', encoding='utf-8') as f:
        json.dump(meta, f, indent=2, ensure_ascii=False)
    log('  manifest %s' % mpath)
    return 0


if __name__ == '__main__':
    sys.exit(main())
