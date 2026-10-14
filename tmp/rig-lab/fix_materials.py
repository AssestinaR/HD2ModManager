# -*- coding: utf-8 -*-
"""Rebuild ONLY the Material entries of an already-normalised package.

Why surgical instead of re-running the whole pipeline: the unit side of the
deployed RE-2310 package is already game-validated (model, skeleton, shadows,
hidden placeholders all confirmed).  `armor-normalize` does not touch materials,
so the material defect can be repaired in place and every Unit / Bone / Texture
payload stays byte-for-byte identical.

Round-60 second half: the texture-table layout fix (M1) reached the game and
produced *zero* visual change.  Comparing against a mod that actually renders
-- 【4.28】Chiffon-LUNALICE替换RE-2310 -- shows why: its two visible armour units
(the very ids we replace, 0xcae35ee15168145f / 0x9841b16544c8fbae) use the
**`advanced`** template, while we shipped **`basic`** ("Sourced from a trash bag
prop", per the SDK's own Global_Materials table).  The slot *keys* do not
overlap at all:

    ours  (basic)     756f6fa6 ac652e43 caed6cd6
    armour family     0f5ff78d a17b45a8 81d4c49d c2eb8d6e ... 6834aa9b

so no reference fix can ever help.  This tool therefore rebuilds the 14
materials from the clean SDK `advanced` template, filling exactly the three
slots the SDK labels for that template:

    slot 2  "Normal/AO/Roughness"   <- shared_flat_normal
    slot 3  "Metallic"              <- shared_pbr, re-pixeled to near-black
    slot 5  "Color/Emission Mask"   <- this group's base colour, alpha forced 0

The alpha-0 step is copied from the working reference mod: its slot-5 texture
measures (209,197,199, **0**) -- a bright albedo whose alpha is zero, i.e. the
emission mask is off.  Our source albedos carry alpha 170..255, which as an
emission mask would make the whole armour glow.

Inputs
    --src       the deployed (or any) patch_0
    --manifest  the rig-lab build manifest that lists every material name and
                every texture name -> id (used to recover the intended texture
                identity, never read back from the corrupted material)
    --template  clean SDK template: basic | basic+ | advanced
    --pbr-b     B channel of the shared texture (basic/basic+ AO control)
    --metallic  RGBA of the shared texture when --template advanced
    --alpha-zero / --no-alpha-zero

Outputs
    <out>/9ba626afa44a3aa3.patch_0[.gpu_resources]
    <out>/<name>.manifest.json
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import numpy as np

import hd2_asset as A
import hd2_patch as HP
import hd2_patchw as PW
import hd2_texpack as TP

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
TEXCONV = os.path.join(SDK, 'deps', 'texconv.exe')
TEMPLATE_FILE = {'basic': 'basic.material', 'basic+': 'basic+.material',
                 'advanced': 'advanced.material'}

# template -> the three slots the group's ids go into, in the order
# (shared, base colour, normal).  `basic`/`basic+` are the 3-slot prop family
# (PBR / Base Color / Normal); `advanced` is the UI-capable armour family the
# shipping reference mod uses (Metallic / Color+Emission / Normal).
TEMPLATE_SLOTS = {
    'basic': (0, 1, 2),
    'basic+': (0, 1, 2),
    'advanced': (3, 5, 2),
}
# slots whose texture the SDK labels, for the log line
SLOT_LABEL = {
    'basic': ('PBR', 'Base Color', 'Normal'),
    'basic+': ('PBR', 'Base Color', 'Normal'),
    'advanced': ('Metallic', 'Color/Emission Mask', 'Normal'),
}

PATCH_NAME = '9ba626afa44a3aa3.patch_0'
PBR_ROUGHNESS = 0.62
DEFAULT_METALLIC = (6, 6, 6, 255)   # the reference mod's metallic map measures 6.4
TEX_ID_PREFIX = 0xB


def make_id(prefix, name):
    """Must match build_patch.make_id -- asserted against the baseline below."""
    h = hashlib.sha256(name.encode('utf-8')).digest()
    v = int.from_bytes(h[:8], 'little') & 0x0FFFFFFFFFFFFFFF
    return ((prefix & 0xF) << 60) | v


def log(m=''):
    print(m, flush=True)


def build_expected(manifest, pbr_b, metallic):
    """material id -> [shared_id, base_id, normal_id], plus the texture map."""
    tex_by_name = {t['name']: int(t['id'], 16) for t in manifest['textures']}
    base_nrm = tex_by_name['shared_flat_normal']

    # self-check: our reimplementation of make_id must reproduce the shipped id
    assert make_id(TEX_ID_PREFIX, 'tex/shared_pbr') == tex_by_name['shared_pbr'], \
        'make_id no longer matches build_patch.py'
    assert make_id(TEX_ID_PREFIX, 'tex/shared_flat_normal') == base_nrm

    # The shared texture keeps its id across every control: the slot identity is
    # "the shared map", and only its pixels change.  Keeping the id stable means
    # every entry maps 1:1 onto the baseline, so a candidate diff is a clean
    # summary instead of an id churn.
    shared_id = tex_by_name['shared_pbr']

    out, bases = {}, {}
    for m in manifest['materials']:
        stem = m['name'].replace('\\', '/').rsplit('/', 1)[-1]
        key = 'base/' + stem
        if key not in tex_by_name:
            # Two fallbacks build_patch.py may have taken when a slot's VTF is
            # absent from every pack:
            #   * engine built-in (`$basetexture "black"`) -> texture `solid/<n>`
            #   * genuinely missing                    -> texture `flat_white`
            alt = None
            if 'solid/' + stem in tex_by_name:
                alt = tex_by_name['solid/' + stem]
            elif 'flat_white' in tex_by_name:
                alt = tex_by_name['flat_white']
            if alt is not None:
                log('  [note] material %r -> fallback texture (no source VTF)' % m['name'])
                out[int(m['id'], 16)] = [shared_id, alt, base_nrm]
                bases[alt] = stem
                continue
            raise KeyError('manifest has no texture %r for material %r'
                           % (key, m['name']))
        out[int(m['id'], 16)] = [shared_id, tex_by_name[key], base_nrm]
        bases[tex_by_name[key]] = stem
    return out, dict(shared_id=shared_id, nrm_id=base_nrm,
                     tex_by_name=tex_by_name, bases=bases)


# ------------------------------------------------------------------ texture ops
def _run(cmd):
    p = subprocess.run(cmd, capture_output=True)
    if p.returncode != 0:
        raise RuntimeError('%s failed (%d)\n%s\n%s'
                           % (os.path.basename(cmd[0]), p.returncode,
                              p.stdout.decode('utf-8', 'replace'),
                              p.stderr.decode('utf-8', 'replace')))
    return p


def solid_gpu(rgba, dxgi):
    """4x4 solid texture -> (148-byte DX10 header, mip0 pixels)."""
    hdr, gpu = TP.solid_texture(4, 4, rgba, dxgi=dxgi)
    return bytes(hdr), gpu


def zero_alpha(dds_header, gpu, tmpdir, tag):
    """Re-encode `gpu` (BC7) with the alpha channel forced to 0.

    Only the pixel payload changes; the caller keeps the original TOC, so the
    entry's format / dimensions / mip count are untouched.
    """
    src = os.path.join(tmpdir, tag + '.dds')
    with open(src, 'wb') as f:
        f.write(bytes(dds_header) + bytes(gpu))
    _run([TEXCONV, '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
          '-m', '1', '-o', tmpdir, src])
    png = os.path.join(tmpdir, tag + '.png')
    if not os.path.exists(png):
        raise RuntimeError('alpha-zero: could not decode %s' % tag)
    from PIL import Image
    im = Image.open(png).convert('RGBA')
    arr = np.array(im)
    arr[:, :, 3] = 0
    png2 = os.path.join(tmpdir, tag + '_a0.png')
    Image.fromarray(arr, 'RGBA').save(png2)
    _run([TEXCONV, '-nologo', '-y', '-f', 'BC7_UNORM_SRGB', '-m', '1',
          '-o', tmpdir, png2])
    dds = os.path.join(tmpdir, tag + '_a0.dds')
    data = open(dds, 'rb').read()
    if data[84:88] != b'DX10':
        raise RuntimeError('alpha-zero: texconv emitted no DX10 header for %s' % tag)
    return data[A.DDS_HEADER_SIZE:]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--manifest', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--name', default='lunalice-m')
    ap.add_argument('--template', choices=tuple(TEMPLATE_FILE), default='basic')
    ap.add_argument('--pbr-b', type=int, default=0)
    ap.add_argument('--metallic', default=','.join(str(v) for v in DEFAULT_METALLIC),
                    help='RGBA of the shared 4x4 texture when --template advanced')
    ap.add_argument('--alpha-zero', dest='alpha_zero', action='store_true',
                    default=None,
                    help='force the base colour alpha to 0 (default: on for '
                         '--template advanced, off otherwise)')
    ap.add_argument('--no-alpha-zero', dest='alpha_zero', action='store_false')
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    if args.alpha_zero is None:
        args.alpha_zero = (args.template == 'advanced')
    metallic = tuple(int(x) for x in args.metallic.split(','))
    if len(metallic) != 4:
        raise SystemExit('--metallic wants four comma-separated bytes')

    manifest = json.load(open(args.manifest, encoding='utf-8'))
    expected, info = build_expected(manifest, args.pbr_b, metallic)
    template = open(os.path.join(SDK, 'materials', TEMPLATE_FILE[args.template]),
                    'rb').read()
    slots = TEMPLATE_SLOTS[args.template]
    labels = SLOT_LABEL[args.template]
    if args.template == 'advanced':
        shared_rgba = metallic
    else:
        shared_rgba = (0, int(PBR_ROUGHNESS * 255), args.pbr_b & 0xFF, 255)

    log('=' * 78)
    log('fix_materials -- %s' % args.src)
    log('  template %s  slots shared/base/normal = %s  (%s)'
        % (args.template, slots, ' / '.join(labels)))
    log('  shared texture rgba %s   alpha-zero %s   shared id %016x'
        % (shared_rgba, args.alpha_zero, info['shared_id']))

    entries, raw = HP.scan_entries(args.src)
    log('  source: %d entries, %d bytes' % (len(entries), len(raw)))

    base_ids = set(info['bases'])
    tmpdir = tempfile.mkdtemp(prefix='matfix')
    out_entries = []
    replaced = set()          # indices we deliberately rewrote
    seen_mat = seen_shared = seen_base = 0
    try:
        for e in entries:
            pl = HP.entry_payload(e, args.src)
            if e['type_id'] == A.MATERIAL_TYPE_ID:
                want = expected.get(e['file_id'])
                if want is None:
                    raise KeyError('material 0x%016x is not in the manifest' % e['file_id'])
                m = A.Material.read(template)
                if len(want) != len(slots):
                    raise AssertionError('template exposes %d slots, group carries %d ids'
                                         % (len(slots), len(want)))
                for slot, tid in zip(slots, want):
                    m.set_texture(slot, tid)
                blob = m.write()
                # the template decides the payload size (basic 512 B, basic+ 416 B,
                # advanced 1408 B); the container is rewritten from scratch so
                # shifted toc offsets are fine as long as the entry table is
                # consistent -- which write_patch guarantees and the checks below
                # re-verify.
                out_entries.append(PW.Entry(e['type_id'], e['file_id'], blob,
                                            pl['stream'], pl['gpu'],
                                            e['unknown1'], e['unknown2'],
                                            note='material:%s' % args.template))
                replaced.add(e['index'])
                seen_mat += 1
            elif e['type_id'] == A.TEXTURE_TYPE_ID and e['file_id'] == info['shared_id']:
                hdr, gpu = solid_gpu(shared_rgba, TP.DXGI_BC7_UNORM)
                t = A.Texture()
                t.unk_id = 0
                t.dds_header = bytearray(hdr)
                t.pixels = gpu
                assert t.write_toc() == pl['toc'], 'shared texture toc header changed'
                assert len(gpu) == len(pl['gpu']), 'shared texture gpu size changed'
                out_entries.append(PW.Entry(e['type_id'], e['file_id'],
                                            pl['toc'], pl['stream'], gpu,
                                            e['unknown1'], e['unknown2'],
                                            note='texture:shared'))
                replaced.add(e['index'])
                seen_shared += 1
            elif e['type_id'] == A.TEXTURE_TYPE_ID and e['file_id'] in base_ids \
                    and args.alpha_zero:
                t = A.Texture.read(pl['toc'], pl['gpu'])
                new_gpu = zero_alpha(t.dds_header, pl['gpu'], tmpdir,
                                     'b%016x' % e['file_id'])
                if len(new_gpu) != len(pl['gpu']):
                    raise AssertionError(
                        'alpha-zero changed gpu size of 0x%016x (%d -> %d)'
                        % (e['file_id'], len(pl['gpu']), len(new_gpu)))
                out_entries.append(PW.Entry(e['type_id'], e['file_id'],
                                            pl['toc'], pl['stream'], new_gpu,
                                            e['unknown1'], e['unknown2'],
                                            note='texture:base-alpha0'))
                replaced.add(e['index'])
                seen_base += 1
            else:
                out_entries.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'],
                                            pl['stream'], pl['gpu'],
                                            e['unknown1'], e['unknown2'],
                                            note='verbatim'))
    finally:
        shutil.rmtree(tmpdir, ignore_errors=True)

    log('  replaced %d materials, %d shared texture, %d base textures (alpha)'
        % (seen_mat, seen_shared, seen_base))
    if seen_mat != len(expected):
        raise AssertionError('replaced %d materials but the manifest lists %d'
                             % (seen_mat, len(expected)))
    if seen_shared != 1:
        raise AssertionError('shared texture replaced %d times' % seen_shared)
    if args.alpha_zero and seen_base != len(base_ids):
        raise AssertionError('alpha-zero wanted %d base textures, saw %d'
                             % (len(base_ids), seen_base))

    if args.dry_run:
        log('  dry run: not writing')
        return 0

    res = PW.write_patch(out_entries, args.src, args.out, PATCH_NAME)
    log('  wrote %s (%d B) + gpu (%d B)'
        % (res['toc'], res['toc_size'], res['gpu_size']))

    # container sanity: every entry we did NOT rewrite must come back byte
    # identical, with the same gpu offset.  Replaced entries are allowed to
    # differ (and a template swap may change their toc size).
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
        assert a['gpu_offset'] == b['gpu_offset'], \
            'gpu offset of verbatim entry %d moved' % a['index']
    assert res['gpu_size'] == os.path.getsize(args.src + '.gpu_resources'), \
        'gpu blob size changed'

    meta = dict(schema='hd2mod.rig-lab-materialfix', sourcePatch=args.src,
                sourceManifest=args.manifest, template=args.template,
                templateSlots=list(slots), slotLabels=list(labels),
                sharedRGBA=list(shared_rgba), alphaZero=bool(args.alpha_zero),
                pbrRGBA=[0, int(PBR_ROUGHNESS * 255), args.pbr_b & 0xFF, 255],
                materialCount=seen_mat, replacedShared=seen_shared,
                replacedBaseTextures=seen_base,
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
