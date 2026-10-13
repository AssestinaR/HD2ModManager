# -*- coding: utf-8 -*-
"""Rebuild ONLY the Material entries of an already-normalised package.

Why surgical instead of re-running the whole pipeline: the unit side of the
deployed RE-2310 package is already game-validated (model, skeleton, shadows,
hidden placeholders all confirmed).  `armor-normalize` does not touch materials,
so the material defect can be repaired in place and every Unit / Bone / Texture
payload stays byte-for-byte identical.  This is exactly what the round-60 review
asked for: "keep the existing native ids / body / placeholder structure, only
modify the corresponding resources".

Inputs
    --src       the deployed (or any) patch_0
    --manifest  the rig-lab build manifest that lists every material name and
                every texture name -> id (used to recover the intended texture
                identity, never read back from the corrupted material)
    --template  clean SDK template: basic | basic+ | advanced(not wired)
    --pbr-b     B channel of the shared PBR texture (0 = round-59, 255 = M2/M3)

Outputs
    <out>/9ba626afa44a3aa3.patch_0[.gpu_resources]
    <out>/<name>.manifest.json
"""
import argparse
import hashlib
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_asset as A
import hd2_patch as HP
import hd2_patchw as PW
import hd2_texpack as TP

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
TEMPLATE_FILE = {'basic': 'basic.material', 'basic+': 'basic+.material',
                 'advanced': 'advanced.material'}
SLOTS_3 = (0, 1, 2)                 # basic / basic+ : PBR, Base Color, Normal
PATCH_NAME = '9ba626afa44a3aa3.patch_0'
PBR_ROUGHNESS = 0.62
TEX_ID_PREFIX = 0xB


def make_id(prefix, name):
    """Must match build_patch.make_id -- asserted against the baseline below."""
    h = hashlib.sha256(name.encode('utf-8')).digest()
    v = int.from_bytes(h[:8], 'little') & 0x0FFFFFFFFFFFFFFF
    return ((prefix & 0xF) << 60) | v


def log(m=''):
    print(m, flush=True)


def build_expected(manifest, pbr_b):
    """material name -> [pbr_id, base_id, normal_id], plus the texture map."""
    tex_by_name = {t['name']: int(t['id'], 16) for t in manifest['textures']}
    base_nrm = tex_by_name['shared_flat_normal']

    # self-check: our reimplementation of make_id must reproduce the shipped id
    assert make_id(TEX_ID_PREFIX, 'tex/shared_pbr') == tex_by_name['shared_pbr'], \
        'make_id no longer matches build_patch.py'
    assert make_id(TEX_ID_PREFIX, 'tex/shared_flat_normal') == base_nrm

    # The PBR texture keeps its id across the AO control: the slot identity is
    # "the shared PBR map", and only its pixels change.  Keeping the id stable
    # means every entry maps 1:1 onto the baseline, so a candidate diff is a
    # clean two-line summary (14 materials + 1 texture) instead of an id churn.
    pbr_id = tex_by_name['shared_pbr']
    pbr_name = 'shared_pbr'

    out = {}
    for m in manifest['materials']:
        stem = m['name'].replace('\\', '/').rsplit('/', 1)[-1]
        key = 'base/' + stem
        if key not in tex_by_name:
            raise KeyError('manifest has no texture %r for material %r'
                           % (key, m['name']))
        out[int(m['id'], 16)] = [pbr_id, tex_by_name[key], base_nrm]
    return out, dict(pbr_id=pbr_id, pbr_name=pbr_name,
                     nrm_id=base_nrm, tex_by_name=tex_by_name)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--manifest', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--name', default='lunalice-m')
    ap.add_argument('--template', choices=tuple(TEMPLATE_FILE), default='basic')
    ap.add_argument('--pbr-b', type=int, default=0)
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    manifest = json.load(open(args.manifest, encoding='utf-8'))
    expected, info = build_expected(manifest, args.pbr_b)
    template = open(os.path.join(SDK, 'materials', TEMPLATE_FILE[args.template]),
                    'rb').read()

    log('=' * 78)
    log('fix_materials -- %s' % args.src)
    log('  template %s   PBR rgba (0, %d, %d, 255)   pbr id %016x'
        % (args.template, int(PBR_ROUGHNESS * 255), args.pbr_b & 0xFF,
           info['pbr_id']))

    entries, raw = HP.scan_entries(args.src)
    log('  source: %d entries, %d bytes' % (len(entries), len(raw)))

    out_entries = []
    replaced = set()          # indices we deliberately rewrote
    seen_mat, seen_tex = 0, 0
    for e in entries:
        pl = HP.entry_payload(e, args.src)
        if e['type_id'] == A.MATERIAL_TYPE_ID:
            want = expected.get(e['file_id'])
            if want is None:
                raise KeyError('material 0x%016x is not in the manifest' % e['file_id'])
            m = A.Material.read(template)
            for slot, tid in zip(SLOTS_3, want):
                m.set_texture(slot, tid)
            blob = m.write()
            # the template decides the payload size (basic 512 B, basic+ 416 B);
            # the container is rewritten from scratch so shifted toc offsets are
            # fine as long as the entry table is consistent -- which write_patch
            # guarantees and the checks below re-verify.
            out_entries.append(PW.Entry(e['type_id'], e['file_id'], blob,
                                        pl['stream'], pl['gpu'],
                                        e['unknown1'], e['unknown2'],
                                        note='material:%s' % args.template))
            replaced.add(e['index'])
            seen_mat += 1
        elif e['type_id'] == A.TEXTURE_TYPE_ID and args.pbr_b != 0 \
                and e['file_id'] == info['tex_by_name']['shared_pbr']:
            # the AO control changes the shared PBR pixels; the id stays put
            hdr, gpu = TP.solid_texture(4, 4, (0, int(PBR_ROUGHNESS * 255),
                                               args.pbr_b & 0xFF, 255),
                                        dxgi=TP.DXGI_BC7_UNORM)
            t = A.Texture()
            t.unk_id = 0
            t.dds_header = bytearray(hdr)
            t.pixels = gpu
            assert t.write_toc() == pl['toc'], 'PBR toc header changed'
            out_entries.append(PW.Entry(e['type_id'], e['file_id'],
                                        pl['toc'], pl['stream'], gpu,
                                        e['unknown1'], e['unknown2'],
                                        note='texture:' + info['pbr_name']))
            replaced.add(e['index'])
            seen_tex += 1
        else:
            out_entries.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'],
                                        pl['stream'], pl['gpu'],
                                        e['unknown1'], e['unknown2'],
                                        note='verbatim'))
    log('  replaced %d materials, %d pbr textures' % (seen_mat, seen_tex))
    if seen_mat != len(expected):
        raise AssertionError('replaced %d materials but the manifest lists %d'
                             % (seen_mat, len(expected)))
    if args.pbr_b != 0 and seen_tex != 1:
        raise AssertionError('PBR control requested but %d textures matched' % seen_tex)

    if args.dry_run:
        log('  dry run: not writing')
        return 0

    res = PW.write_patch(out_entries, args.src, args.out, PATCH_NAME)
    log('  wrote %s (%d B) + gpu (%d B)'
        % (res['toc'], res['toc_size'], res['gpu_size']))

    # container sanity: every entry we did NOT rewrite must come back byte
    # identical, with the same gpu offset.  Replaced entries are allowed to
    # differ (and a 3-slot template swap may change their toc size).
    new_entries, _ = HP.scan_entries(res['toc'])
    assert len(new_entries) == len(entries), 'entry count changed'
    for a, b in zip(entries, new_entries):
        if a['index'] in replaced:
            assert a['type_id'] == b['type_id'], 'type changed at %d' % a['index']
            continue
        if (a['file_id'], a['type_id']) != (b['file_id'], b['type_id']):
            raise AssertionError('entry %d identity changed' % a['index'])
        pa = HP.entry_payload(a, args.src)
        pb = HP.entry_payload(b, res['toc'])
        assert pa == pb, 'verbatim entry %d (0x%016x) changed' % (a['index'], a['file_id'])
        # gpu offsets must not move (identical blobs, same order); toc offsets
        # MAY shift if a replaced payload changed size (basic+ is 416 B, not 512)
        assert a['gpu_offset'] == b['gpu_offset'], \
            'gpu offset of verbatim entry %d moved' % a['index']
    assert res['gpu_size'] == os.path.getsize(args.src + '.gpu_resources'), \
        'gpu blob size changed'

    meta = dict(schema='hd2mod.rig-lab-materialfix', sourcePatch=args.src,
                sourceManifest=args.manifest, template=args.template,
                pbrRGBA=[0, int(PBR_ROUGHNESS * 255), args.pbr_b & 0xFF, 255],
                materialCount=seen_mat, replacedTextures=seen_tex,
                outputPatch=res['toc'], tocBytes=res['toc_size'],
                gpuBytes=res['gpu_size'], entryCount=len(out_entries))
    mpath = os.path.join(args.out, args.name + '.manifest.json')
    os.makedirs(args.out, exist_ok=True)
    with open(mpath, 'w', encoding='utf-8') as f:
        json.dump(meta, f, indent=2, ensure_ascii=False)
    log('  manifest %s' % mpath)
    return 0


if __name__ == '__main__':
    sys.exit(main())
