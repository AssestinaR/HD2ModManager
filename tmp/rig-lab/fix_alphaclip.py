# -*- coding: utf-8 -*-
"""M7 -- give the lace its镂空 back.

What the evidence says (see TRANSPARENCY_BRIEF.md):

  * L4D2 stores the镂空 as the **alpha channel** of the VTF; the VMT only has to
    opt in with `$translucent 1`.  Five of our fourteen source materials do:
    bra_white01_bra_inner, headdress_white01_catears, lace_white01_catears,
    shoes_white01_highheels, shoes_02.
  * HD2 cannot do that in `advanced` at all -- the SDK's own Global_Materials
    table lists "No transparency" as an `advanced` con, and its slot table has
    no alpha slot (slot 5 is "Color/Emission Mask" = emission, not alpha).
  * The HD2 material that *is* built for this is **`alphaclip`**: 3 slots,
    slot 1 is literally called "Alpha Mask", and the wiki states
    "The White of the mask is visible and the black will be transparent."

So: rebuild exactly those five materials from the SDK `alphaclip` template, add
one 2048x2048 grayscale mask texture per material (the source VTF's alpha), and
leave the other nine `advanced` materials, every Unit/Bone payload and every
other texture byte-for-byte untouched.

usage:
  fix_alphaclip.py --src <patch_0> --out <dir> [--name lunalice-m7] [--dry-run]
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_asset as A
import hd2_patch as HP
import hd2_patchw as PW
import hd2_source as S
import hd2_texpack as TP
import hd2_vtf as V

SDK = r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0'
TEXCONV = os.path.join(SDK, 'deps', 'texconv.exe')
ALPHACLIP_TEMPLATE = os.path.join(SDK, 'materials', 'alphaclip.material')

PATCH_NAME = '9ba626afa44a3aa3.patch_0'
TEX_ID_PREFIX = 0xB

# material id -> (source VTF stem, why)
TARGETS = {
    0xa65530f529ebb019: ('headdress_white01_catears',
                         '$translucent 1 in headdress_white01_lace_headdress.vmt'),
    0xa58d8407e8deb2c0: ('lace_white01_catears',
                         '$translucent 1 in lace_white01_skirt.vmt'),
    0xa241d3a3f8d62d33: ('bra_white01_bra_inner',
                         '$translucent 1 in bra_white01_lace_dress_corset.vmt'),
    0xab373402aa0b5227: ('shoes_white01_highheels',
                         '$translucent 1 in shoes_white01_leg_ribbon_right.vmt'),
    0xab6b34729e37c2f9: ('shoes_02',
                         '$translucent 1 in shoes_02_lace.vmt'),
}
# the flat 4x4 normal/AO/roughness map every material already shares
FLAT_NORMAL_ID = 0xba1a40900756a7f3

# alphaclip slot order (SDK TextureTypeLookup)
SLOT_NORMAL, SLOT_MASK, SLOT_COLOR = 0, 1, 2


def make_id(prefix, name):
    """Must match build_patch.make_id (asserted against the shipped ids)."""
    h = hashlib.sha256(name.encode('utf-8')).digest()
    v = int.from_bytes(h[:8], 'little') & 0x0FFFFFFFFFFFFFFF
    return ((prefix & 0xF) << 60) | v


def log(m=''):
    print(m, flush=True)


def _run(cmd):
    p = subprocess.run(cmd, capture_output=True)
    if p.returncode != 0:
        raise RuntimeError('%s failed (%d)\n%s\n%s'
                           % (os.path.basename(cmd[0]), p.returncode,
                              p.stdout.decode('utf-8', 'replace'),
                              p.stderr.decode('utf-8', 'replace')))
    return p


def source_alpha_png(packs, stem, tmpdir):
    """Decode the source VTF mip0 to RGBA (native format, one texconv step) and
    return the alpha plane."""
    from PIL import Image
    raw = packs.vtf_bytes(stem)
    if raw is None:
        raise KeyError('source VTF %r not found in the packs' % stem)
    v = V.VTF.read(raw)
    if v.hi_format not in TP.VTF_TO_WRAPPER_DXGI:
        raise ValueError('%s is %s -- no alpha channel to lift' % (stem, v.format_name))
    dds = os.path.join(tmpdir, stem + '_src.dds')
    with open(dds, 'wb') as f:
        f.write(TP._wrapper_dds(v, TP.VTF_TO_WRAPPER_DXGI[v.hi_format],
                                v.width, v.height, v.mips[0], None))
    _run([TEXCONV, '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
          '-m', '1', '-o', tmpdir, dds])
    png = os.path.join(tmpdir, stem + '_src.png')
    if not os.path.exists(png):
        raise RuntimeError('could not decode the source VTF %r' % stem)
    arr = np.asarray(Image.open(png).convert('RGBA'), np.uint8)
    return arr[:, :, 3], arr[:, :, :3], v


def build_mask_texture(alpha, stem, tmpdir, preview_dir=None, rgb=None):
    """Grayscale mask (white = keep) -> (Texture asset, gpu bytes)."""
    from PIL import Image
    h, w = alpha.shape
    rgb8 = np.dstack([alpha, alpha, alpha])
    a = np.full((h, w, 1), 255, np.uint8)
    png = os.path.join(tmpdir, stem + '_mask.png')
    Image.fromarray(np.concatenate([rgb8, a], axis=2), 'RGBA').save(png)
    if preview_dir is not None:
        # what the shader will do at OpacityThreshold 0.5, side by side
        s = 512
        binv = np.where(alpha >= 128, 255, 0).astype(np.uint8)
        base = rgb if rgb is not None else rgb8
        tiles = [Image.fromarray(base, 'RGB').resize((s, s), Image.LANCZOS),
                 Image.fromarray(np.dstack([alpha] * 3), 'RGB').resize((s, s), Image.LANCZOS),
                 Image.fromarray(np.dstack([binv] * 3), 'RGB').resize((s, s), Image.NEAREST)]
        sheet = Image.new('RGB', (s * 3, s))
        for i, t in enumerate(tiles):
            sheet.paste(t, (i * s, 0))
        sheet.save(os.path.join(preview_dir, 'mask_%s.png' % stem))
    _run([TEXCONV, '-nologo', '-y', '-f', 'BC7_UNORM', '-m', '1', '-o', tmpdir, png])
    dds = os.path.join(tmpdir, stem + '_mask.dds')
    data = open(dds, 'rb').read()
    if data[84:88] != b'DX10':
        raise RuntimeError('texconv emitted no DX10 header for the mask of %s' % stem)
    hdr = bytearray(data[:A.DDS_HEADER_SIZE])
    import struct
    struct.pack_into('<I', hdr, 12, h)
    struct.pack_into('<I', hdr, 16, w)
    struct.pack_into('<I', hdr, 20, w)
    struct.pack_into('<I', hdr, 28, 1)
    struct.pack_into('<I', hdr, 128, TP.DXGI_BC7_UNORM)
    struct.pack_into('<I', hdr, 140, 1)
    px = data[A.DDS_HEADER_SIZE:]
    t = A.Texture()
    t.unk_id = 0
    t.dds_header = hdr
    t.pixels = px
    return t, px


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--name', default='lunalice-m7')
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    entries, raw = HP.scan_entries(args.src)
    log('=' * 78)
    log('fix_alphaclip -- %s' % args.src)
    log('  source: %d entries, %d bytes' % (len(entries), len(raw)))

    # ---- the new texture ids, and a collision guard ------------------------
    mask_ids = {}
    for mid, (stem, _why) in TARGETS.items():
        tid = make_id(TEX_ID_PREFIX, 'tex/mask/' + stem)
        mask_ids[mid] = tid
    existing = {e['file_id'] for e in entries}
    clash = {m: t for m, t in mask_ids.items() if t in existing}
    if clash:
        raise AssertionError('mask ids collide with shipped ids: %r' % clash)
    if len(set(mask_ids.values())) != len(mask_ids):
        raise AssertionError('mask ids are not unique')

    # ---- material lookup: current base-colour id per target ----------------
    mat_slots = {}
    for e in entries:
        if e['type_id'] != A.MATERIAL_TYPE_ID:
            continue
        m = A.Material.read(HP.entry_payload(e, args.src)['toc'])
        mat_slots[e['file_id']] = m

    template = A.Material.read(open(ALPHACLIP_TEMPLATE, 'rb').read())
    log('  alphaclip template: ntex=%d nvar=%d parent=%s'
        % (len(template.tex_ids), len(template.variables), template.parent_name))
    if [hex(k) for k in template.tex_unks] != ['0x1d57dcf3', '0xcbde381b', '0x848ba63b']:
        raise AssertionError('alphaclip template slot keys are not the expected three')

    tmpdir = tempfile.mkdtemp(prefix='alphaclip')
    packs = S.Packs()
    os.makedirs(os.path.join(args.out, 'preview'), exist_ok=True)
    try:
        # ---- build the five masks -----------------------------------------
        new_tex_entries = []
        report = []
        for mid, (stem, why) in sorted(TARGETS.items()):
            if mid not in mat_slots:
                raise KeyError('material 0x%016x is not in the patch' % mid)
            cur = mat_slots[mid]
            if cur.parent_name != 'advanced':
                raise AssertionError('0x%016x is %s, expected the M6 advanced material'
                                     % (mid, cur.parent_name))
            base_id = cur.tex_ids[5]              # advanced slot 5 = AlbedoEmissive
            alpha, src_rgb, v = source_alpha_png(packs, stem, tmpdir)
            tid = mask_ids[mid]
            t, px = build_mask_texture(alpha, stem, tmpdir,
                                       preview_dir=os.path.join(args.out, 'preview'),
                                       rgb=src_rgb)
            new_tex_entries.append(PW.Entry(
                A.TEXTURE_TYPE_ID, tid, t.write_toc(), b'', px,
                unknown1=0, unknown2=0, note='texture:mask:%s' % stem))
            st = dict(zero=100.0 * float((alpha == 0).mean()),
                      full=100.0 * float((alpha == 255).mean()),
                      cut=100.0 * float((alpha < 128).mean()))
            report.append(dict(material='0x%016x' % mid, stem=stem, why=why,
                               maskId='%016x' % tid, baseColorId='%016x' % base_id,
                               size='%dx%d' % (v.width, v.height),
                               sourceFormat=v.format_name,
                               zeroPct=round(st['zero'], 2),
                               fullPct=round(st['full'], 2),
                               cutAt0_5Pct=round(st['cut'], 2)))
            log('  mask %-28s %s  id=%016x  <- %s alpha (0:%.1f%% 255:%.1f%% '
                'cut@0.5:%.1f%%)'
                % (stem, '%dx%d' % (v.width, v.height), tid, v.format_name,
                   st['zero'], st['full'], st['cut']))
    finally:
        shutil.rmtree(tmpdir, ignore_errors=True)

    # ---- rebuild the container -------------------------------------------
    out_entries = []
    replaced = set()
    seen_mat = 0
    for e in entries:
        pl = HP.entry_payload(e, args.src)
        if e['type_id'] == A.MATERIAL_TYPE_ID and e['file_id'] in TARGETS:
            m = A.Material.read(open(ALPHACLIP_TEMPLATE, 'rb').read())
            m.set_texture(SLOT_NORMAL, FLAT_NORMAL_ID)
            m.set_texture(SLOT_MASK, mask_ids[e['file_id']])
            m.set_texture(SLOT_COLOR, mat_slots[e['file_id']].tex_ids[5])
            out_entries.append(PW.Entry(e['type_id'], e['file_id'], m.write(),
                                        pl['stream'], pl['gpu'],
                                        e['unknown1'], e['unknown2'],
                                        e['unknown3'], e['unknown4'],
                                        note='material:alphaclip'))
            replaced.add(e['index'])
            seen_mat += 1
        else:
            out_entries.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'],
                                        pl['stream'], pl['gpu'],
                                        e['unknown1'], e['unknown2'],
                                        e['unknown3'], e['unknown4'],
                                        note='verbatim'))
    out_entries.extend(new_tex_entries)
    if seen_mat != len(TARGETS):
        raise AssertionError('rebuilt %d materials, expected %d' % (seen_mat, len(TARGETS)))

    log('  rebuilt %d materials, added %d mask textures' % (seen_mat, len(new_tex_entries)))

    if args.dry_run:
        log('  dry run: not writing')
        return 0

    res = PW.write_patch(out_entries, args.src, args.out, PATCH_NAME)
    log('  wrote %s (%d B) + gpu (%d B)'
        % (res['toc'], res['toc_size'], res['gpu_size']))

    # ---- verification -----------------------------------------------------
    new_entries, _ = HP.scan_entries(res['toc'])
    if len(new_entries) != len(entries) + len(new_tex_entries):
        raise AssertionError('entry count %d != %d + %d'
                             % (len(new_entries), len(entries), len(new_tex_entries)))
    by_key = {(e['type_id'], e['file_id']): e for e in new_entries}
    for a in entries:
        if a['index'] in replaced:
            continue
        b = by_key.get((a['type_id'], a['file_id']))
        if b is None:
            raise AssertionError('entry 0x%016x vanished' % a['file_id'])
        pa = HP.entry_payload(a, args.src)
        pb = HP.entry_payload(b, res['toc'])
        if pa != pb:
            raise AssertionError('verbatim entry 0x%016x changed' % a['file_id'])
        if a['gpu_offset'] != b['gpu_offset']:
            raise AssertionError('gpu offset of 0x%016x moved' % a['file_id'])
    if res['gpu_size'] < os.path.getsize(args.src + '.gpu_resources'):
        raise AssertionError('gpu blob shrank')

    # every rebuilt material must read back as alphaclip with the right slots
    for mid in TARGETS:
        e = by_key[(A.MATERIAL_TYPE_ID, mid)]
        m = A.Material.read(HP.entry_payload(e, res['toc'])['toc'])
        if m.parent_name != 'alphaclip':
            raise AssertionError('0x%016x did not come back as alphaclip' % mid)
        if m.tex_ids != [FLAT_NORMAL_ID, mask_ids[mid], mat_slots[mid].tex_ids[5]]:
            raise AssertionError('0x%016x slots are wrong' % mid)
    # and every mask must decode back to the source alpha
    from PIL import Image
    tmp2 = tempfile.mkdtemp(prefix='alphaclipcheck')
    try:
        for mid, (stem, _why) in sorted(TARGETS.items()):
            e = by_key[(A.TEXTURE_TYPE_ID, mask_ids[mid])]
            pl = HP.entry_payload(e, res['toc'])
            t = A.Texture.read(pl['toc'], pl['gpu'])
            dds = os.path.join(tmp2, stem + '.dds')
            with open(dds, 'wb') as f:
                f.write(t.to_dds())
            _run([TEXCONV, '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
                  '-m', '1', '-o', tmp2, dds])
            got = np.asarray(Image.open(os.path.join(tmp2, stem + '.png')).convert('RGBA'),
                             np.uint8)[:, :, 0].astype(np.int32)
            packs2 = packs
            alpha, _rgb, _v = source_alpha_png(packs2, stem, tmp2)
            d = np.abs(got - alpha.astype(np.int32))
            log('  verify %-28s mask max|d|=%d  mean|d|=%.3f'
                % (stem, int(d.max()), float(d.mean())))
            if d.mean() > 4.0:
                raise AssertionError('mask for %s does not match the source alpha' % stem)
    finally:
        shutil.rmtree(tmp2, ignore_errors=True)

    meta = dict(schema='hd2mod.rig-lab-alphaclip', sourcePatch=args.src,
                template='alphaclip', slotOrder=['NormalXyAoRoughMap',
                                                 'OpacityClipMap', 'BaseColorMetalMap'],
                flatNormalId='%016x' % FLAT_NORMAL_ID,
                opacityThreshold=0.5,
                materials=report,
                outputPatch=res['toc'], tocBytes=res['toc_size'],
                gpuBytes=res['gpu_size'], entryCount=len(out_entries))
    os.makedirs(args.out, exist_ok=True)
    mpath = os.path.join(args.out, args.name + '.manifest.json')
    with open(mpath, 'w', encoding='utf-8') as f:
        json.dump(meta, f, indent=2, ensure_ascii=False)
    log('  manifest %s' % mpath)
    log('  toc sha256  %s' % hashlib.sha256(open(res['toc'], 'rb').read()).hexdigest())
    log('  gpu sha256  %s' % hashlib.sha256(open(res['gpu'], 'rb').read()).hexdigest())
    return 0


if __name__ == '__main__':
    sys.exit(main())
