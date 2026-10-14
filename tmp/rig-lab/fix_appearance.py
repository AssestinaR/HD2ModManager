# -*- coding: utf-8 -*-
"""M8 -- one *data driven* appearance pass, replacing M7's hard-coded list.

Everything is derived from the model's own 92-slot table and the source packs.
No material id, texture name or body region appears anywhere in the decisions.

--------------------------------------------------------------- A. transparency
L4D2 keeps the cut-out in the **alpha channel of the VTF**; the VMT only opts in
(`$translucent 1` = blend, `$alphatest 1` = hard cutout at `$alphatestreference`).
HD2 cannot express that in `advanced` -- its 11-slot table has no alpha slot --
but `alphaclip` can: slot 1 is literally "Alpha Mask" and the wiki says
"The White of the mask is visible and the black will be transparent".

So for every *group* (= one HD2 material, keyed by `$basetexture`, exactly the
key `build_patch.build_materials` uses) we ask two questions:

    opts_in  = any VMT in the group has a live `$translucent` / `$alphatest`
    has_alpha= the group's VTF really carries a non-uniform alpha channel

    opts_in and has_alpha  ->  alphaclip + the alpha as the mask
    otherwise              ->  leave the material alone

`has_alpha` is what keeps the ornament trim honest: those VMTs *do* say
`$alphatest 1`, but their `$basetexture` is a 1x1 RGB888 texel with no alpha at
all, so there is nothing to cut with.

------------------------------------------------------------ B. NekoToon matcap
Every one of the 92 source VMTs is a `NekoToon` shader, and every one of them
adds a **matcap** on top of the base texture (mode 2 = additive):

    appearance = basetexture_rgb  +  matcaptexture_rgb * matcaptint_rgb

We only ever carried `$basetexture`.  For the ornament trim the base is a 1x1
near-black texel (15,15,15) and *the whole look is the matcap*, which is exactly
why those parts render pure black.  Rule, again purely from the data:

    tint = max(matcaptint_rgb) over the group's slots
    tint >= --matcap-min-tint (default 0.9)  ->  the matcap *is* the surface
        albedo    := clamp(base_rgb + matcap_mean_rgb * tint)   (baked average)
        slot 3    := metallic   (--metal-metallic,  default 0.75)
        slot 2 A  := roughness  (--metal-roughness, default 0.35)
    otherwise -> the matcap is only view-dependent shading, which `advanced`
        cannot carry; leave the group byte-for-byte as it is.

The `advanced` slot semantics come from the SDK's own Blender node graph
(`__init__.py :: SetupAdvancedBlenderMaterial`, lines 1490-1521):
    slot 2 "Normal/AO/Roughness"  RGB -> normal, **Alpha -> Roughness**
    slot 3 "Metallic"                  -> BSDF Metallic
    slot 5 "Color/Emission Mask"  RGB -> Base Color, Alpha -> emission strength
which is also exactly how the working reference mod lays its material out.

usage:
  fix_appearance.py --src <patch_0> --out <dir> [--name lunalice-m8]
                    [--metallic 191] [--roughness 89] [--matcap-min-tint 0.9]
                    [--report-only] [--dry-run]
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
from collections import OrderedDict

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_asset as A
import hd2_patch as HP
import hd2_patchw as PW
import hd2_source as S
import hd2_style as ST
import hd2_texpack as TP
import hd2_vtf as V

SDK = r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0'
TEXCONV = os.path.join(SDK, 'deps', 'texconv.exe')
MATERIALS_DIR = os.path.join(SDK, 'materials')
TEMPLATE_FILE = {'advanced': 'advanced.material', 'alphaclip': 'alphaclip.material'}
# advanced slot indices (SDK TextureTypeLookup, confirmed by the node graph)
ADV_NORMAL, ADV_METALLIC, ADV_COLOR = 2, 3, 5
# alphaclip slot indices
AC_NORMAL, AC_MASK, AC_COLOR = 0, 1, 2

PATCH_NAME = '9ba626afa44a3aa3.patch_0'
TEX_ID_PREFIX = 0xB
MAT_ID_PREFIX = 0xA
FLAT_NORMAL_RGB = (128, 128, 255)

# a group only counts as "the VTF has an alpha channel" when the channel is
# actually there and actually varies; a DXT1 map decodes to a flat 255.
ALPHA_MIN_SPREAD = 3.0        # max-min on the 0..255 scale
ALPHA_MIN_COVERAGE = 0.005    # fraction of texels with alpha < 250


# --------------------------------------------------------------------- helpers
def make_id(prefix, name):
    """Must match build_patch.make_id / fix_materials.make_id."""
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


def _num(s, default=0.0):
    if s is None:
        return default
    try:
        return float(str(s).strip().strip('"'))
    except ValueError:
        return default


def _rgb3(s):
    if not s:
        return (0.0, 0.0, 0.0)
    out = []
    for p in str(s).strip().strip('"').strip('[]').replace(',', ' ').split():
        try:
            out.append(float(p))
        except ValueError:
            pass
    while len(out) < 3:
        out.append(0.0)
    return tuple(out[:3])


def active_keys(txt):
    """'$key' -> value for every live (non-comment) line of a VMT."""
    out = OrderedDict()
    for line in (txt or '').splitlines():
        s = line.strip()
        if not s or s.startswith('//') or s in ('{', '}'):
            continue
        if not s.startswith('$'):
            continue
        k, _, v = s.partition(' ')
        v = v.strip()
        if v.startswith('"'):
            end = v.find('"', 1)
            v = v[1:end] if end > 0 else v[1:]
        else:
            cut = v.find('//')
            if cut >= 0:
                v = v[:cut].strip()
            v = v.split()[0] if v.split() else ''
        out[k.lower()] = v
    return out


# ------------------------------------------------------------------ codecs
def encode_array(arr_rgba, dxgi, tmpdir, tag):
    """RGBA array -> (Texture asset, gpu bytes), header normalised like the
    shipping textures (mipCount 1, pitch = width, DXGI = dxgi)."""
    from PIL import Image
    h, w = arr_rgba.shape[:2]
    png = os.path.join(tmpdir, tag + '.png')
    Image.fromarray(arr_rgba, 'RGBA').save(png)
    hdr, px = TP._run_texconv(png, tmpdir, dxgi, w, h)
    struct.pack_into('<I', hdr, 12, h)
    struct.pack_into('<I', hdr, 16, w)
    struct.pack_into('<I', hdr, 20, w)
    struct.pack_into('<I', hdr, 28, 1)
    struct.pack_into('<I', hdr, 128, dxgi)
    struct.pack_into('<I', hdr, 140, 1)
    t = A.Texture()
    t.unk_id = 0
    t.dds_header = hdr
    t.pixels = px
    return t, px


def decode_patch_texture(patch, entry, tmpdir, tag):
    """Texture entry -> (RGBA array, Texture header)."""
    from PIL import Image
    pl = HP.entry_payload(entry, patch)
    t = A.Texture.read(pl['toc'], pl['gpu'])
    dds = os.path.join(tmpdir, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(t.to_dds())
    _run([TEXCONV, '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
          '-m', '1', '-o', tmpdir, dds])
    png = os.path.join(tmpdir, tag + '.png')
    if not os.path.exists(png):
        raise RuntimeError('could not decode texture %016x' % entry['file_id'])
    return np.asarray(Image.open(png).convert('RGBA'), np.uint8), t


def decode_vtf(packs, stem, tmpdir, tag):
    """Source VTF mip0 -> (RGBA array or None, VTF).  None when the format has
    no decodable path (e.g. a bare RGB888 texel with no alpha channel)."""
    from PIL import Image
    raw = packs.vtf_bytes(stem)
    if raw is None:
        return None, None
    v = V.VTF.read(raw)
    if v.hi_format in TP.VTF_TO_WRAPPER_DXGI:
        dds = os.path.join(tmpdir, tag + '.dds')
        with open(dds, 'wb') as f:
            f.write(TP._wrapper_dds(v, TP.VTF_TO_WRAPPER_DXGI[v.hi_format],
                                    v.width, v.height, v.mips[0], None))
        _run([TEXCONV, '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
              '-m', '1', '-o', tmpdir, dds])
        png = os.path.join(tmpdir, tag + '.png')
        if not os.path.exists(png):
            return None, v
    else:
        try:
            png = TP._uncompressed_png(v, tmpdir, tag)
        except Exception:
            return None, v
    return np.asarray(Image.open(png).convert('RGBA'), np.uint8), v


# ------------------------------------------------------------------- analysis
def analyse(tris4, names, packs):
    """-> OrderedDict basetexture -> group dict.  The ONLY inputs are the
    model's slot table and the source packs."""
    used = sorted(int(x) for x in np.unique(tris4[:, 3]))
    groups = OrderedDict()
    for i in used:
        nm = names[i] if 0 <= i < len(names) else None
        txt = packs.vmt_text(nm) if nm else None
        vmt = S.parse_vmt(txt) if txt else {}
        base = vmt.get('$basetexture')
        ak = active_keys(txt)
        g = groups.get(base)
        if g is None:
            g = groups[base] = dict(base=base, slots=[], tris=0, alpha_wanted=False,
                                    why_alpha=[], matcaps=OrderedDict(),
                                    matcap_mode=0, tint=(0.0, 0.0, 0.0))
        g['slots'].append(i)
        g['tris'] += int((tris4[:, 3] == i).sum())
        if _num(ak.get('$translucent')) or _num(ak.get('$alphatest')):
            g['alpha_wanted'] = True
            g['why_alpha'].append('%s(%s)' % (os.path.basename(nm or '?'),
                                              '$translucent' if _num(ak.get('$translucent'))
                                              else '$alphatest'))
        mode = _num(ak.get('$matcap'))
        if mode:
            g['matcap_mode'] = max(g['matcap_mode'], int(mode))
            mt = ak.get('$matcaptexture')
            tint = _rgb3(ak.get('$matcaptint'))
            e = g['matcaps'].get(mt)
            if e is None:
                e = g['matcaps'][mt] = dict(tex=mt, tint=tint, slots=[], tris=0)
            e['slots'].append(i)
            e['tris'] += int((tris4[:, 3] == i).sum())
            g['tint'] = tuple(max(a, b) for a, b in zip(g['tint'], tint))
    return groups


def pick_matcap(g):
    """The matcap that covers the most geometry -- reported for the log."""
    if not g['matcaps']:
        return None
    return max(g['matcaps'].values(), key=lambda e: (e['tris'], len(e['slots'])))


def matcap_average(g, packs, tmpdir, tag):
    """Triangle-weighted mean RGB of every matcap the group uses.

    One HD2 material has to serve the whole group, so the honest aggregate is
    the area-weighted average of the surfaces it covers -- and the group's
    matcaps are exactly the surfaces.  (For the ornament trim the dominant
    matcap already covers 82% of the triangles, so the average is within 7% of
    it; the weighting matters for the general case, not this one.)

    Third-party packs reference matcaps that are simply not shipped (dangling
    `$matcaptexture`); those are skipped -- with a loud note -- and the weights
    renormalised over whatever is actually present.  If nothing decodes we
    still fail, because then there is no colour to fall back on."""
    usable = []
    missing = []
    for e in g['matcaps'].values():
        arr, _v = decode_vtf(packs, e['tex'], tmpdir, tag + '_' + os.path.basename(e['tex']))
        if arr is None:
            missing.append(e)
            continue
        usable.append((e, arr))
    for e in missing:
        log('  [note] matcap %r is not shipped by any pack (%d slots) -- skipped'
            % (os.path.basename(e['tex']), len(e['slots'])))
    total = sum(e['tris'] for e, _a in usable)
    if total <= 0 or not usable:
        raise RuntimeError('matcap group has no decodable matcap (missing: %s)'
                           % ', '.join(os.path.basename(e['tex']) for e in missing))
    acc = np.zeros(3, np.float64)
    parts = []
    for e, arr in usable:
        mean = arr[:, :, :3].reshape(-1, 3).mean(axis=0)
        w = e['tris'] / float(total)
        acc += mean * w
        parts.append(dict(tex=os.path.basename(e['tex']), tris=e['tris'],
                          weight=round(w, 4), tint=[round(x, 3) for x in e['tint']],
                          meanRgb=[round(float(x), 1) for x in mean]))
    for e in missing:
        parts.append(dict(tex=os.path.basename(e['tex']), tris=e['tris'],
                          weight=0.0, tint=[round(x, 3) for x in e['tint']],
                          meanRgb=None, missing=True))
    return acc, parts


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', default=r'E:\mod\Lunalice-RE2310-M6\9ba626afa44a3aa3.patch_0')
    ap.add_argument('--out', required=True)
    ap.add_argument('--name', default='lunalice-m8')
    # ---- source side (round 63) --------------------------------------------
    ap.add_argument('--source-vpk', default=S.ADDONS + S.MODEL_VPK,
                    help='L4D2 model VPK (default: Lunalice_Zoey)')
    ap.add_argument('--source-model', default='survivor_teenangst',
                    help='model stem inside --source-vpk')
    ap.add_argument('--addons', default=S.ADDONS,
                    help='directory holding the L4D2 addon VPKs')
    ap.add_argument('--pack', action='append', default=None, metavar='NAME.vpk',
                    help='a VPK (relative to --addons) that may supply a VMT or VTF; '
                         'repeatable, later packs override earlier ones. '
                         'Default: the Lunalice base set.')
    ap.add_argument('--model-vpk', default=S.MODEL_VPK,
                    help='which of --pack is the model itself')
    ap.add_argument('--metallic', type=int, default=191,
                    help='slot 3 value for a matcap-driven group (0..255)')
    ap.add_argument('--roughness', type=int, default=89,
                    help='slot 2 ALPHA value for a matcap-driven group (0..255)')
    ap.add_argument('--matcap-min-tint', type=float, default=0.9,
                    help='max(matcaptint) at/above which the matcap IS the surface')
    ap.add_argument('--report-only', action='store_true')
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    log('=' * 100)
    log('fix_appearance (M8) -- %s' % args.src)
    log('  knobs: matcap-min-tint %.2f  metallic %d  roughness %d'
        % (args.matcap_min_tint, args.metallic, args.roughness))

    entries, _raw = HP.scan_entries(args.src)
    log('  source: %d entries, %d bytes' % (len(entries), len(_raw)))
    tex_entries = {e['file_id']: e for e in entries if e['type_id'] == A.TEXTURE_TYPE_ID}
    mat_entries = {e['file_id']: e for e in entries if e['type_id'] == A.MATERIAL_TYPE_ID}

    # ---- the model's own slot table ---------------------------------------
    # Round 63: source is parameterised so the same appearance pass runs on any
    # L4D2 pack.  Defaults reproduce the Lunalice M8 build exactly.
    _md, mdl, _v, tris4, _s, _sh = ST.load_source(args.source_vpk, args.source_model)
    tris4 = np.asarray(tris4, np.int64)
    names, cds, _row = S.load_material_slots(mdl)
    packs = S.Packs(addons=args.addons, packs=args.pack, model_vpk=args.model_vpk)
    log('  model: %d slots referenced, cdtextures %s' % (len(np.unique(tris4[:, 3])), cds))

    groups = analyse(tris4, names, packs)

    # ---- the template contracts ------------------------------------------
    tpl = {k: A.Material.read(open(os.path.join(MATERIALS_DIR, f), 'rb').read())
           for k, f in TEMPLATE_FILE.items()}
    if tpl['alphaclip'].parent_name != 'alphaclip' or len(tpl['alphaclip'].tex_ids) != 3:
        raise AssertionError('alphaclip template is not the expected 3-slot one')
    if tpl['advanced'].parent_name != 'advanced' or len(tpl['advanced'].tex_ids) != 11:
        raise AssertionError('advanced template is not the expected 11-slot one')
    log('  templates: alphaclip %d slots / advanced %d slots'
        % (len(tpl['alphaclip'].tex_ids), len(tpl['advanced'].tex_ids)))

    tmpdir = tempfile.mkdtemp(prefix='appearance')
    os.makedirs(os.path.join(args.out, 'preview'), exist_ok=True)
    try:
        decisions = []
        log('')
        log('%-34s %5s %5s  %-9s %-6s  %s'
            % ('group (= $basetexture)', 'slots', 'tris', 'template', 'matcap', 'note'))
        log('-' * 100)
        for base, g in groups.items():
            stem = os.path.basename(base or 'untextured')
            mat_id = make_id(MAT_ID_PREFIX, 'mat/' + (base or 'untextured'))
            mat = mat_entries.get(mat_id)
            if mat is None:
                raise KeyError('no material 0x%016x for group %r' % (mat_id, base))
            cur = A.Material.read(HP.entry_payload(mat, args.src)['toc'])
            if cur.parent_name != 'advanced':
                raise AssertionError('%s is %s, expected the all-advanced M6 source'
                                     % (stem, cur.parent_name))

            # ---- A. transparency -----------------------------------------
            arr, vtf = decode_vtf(packs, stem, tmpdir, 'g_' + stem)
            has_alpha = False
            alpha = None
            if arr is not None:
                alpha = arr[:, :, 3].astype(np.int32)
                spread = float(alpha.max() - alpha.min())
                cover = float((alpha < 250).mean())
                has_alpha = spread >= ALPHA_MIN_SPREAD and cover >= ALPHA_MIN_COVERAGE
                if g['alpha_wanted'] and not has_alpha:
                    note_alpha = ('opts in but %s has no usable alpha '
                                  '(spread %.0f, cover %.3f%%)'
                                  % (vtf.format_name if vtf else '?', spread, cover * 100))
                else:
                    note_alpha = ''
            else:
                note_alpha = 'no decodable VTF'
            use_alphaclip = bool(g['alpha_wanted'] and has_alpha)

            # ---- B. matcap ------------------------------------------------
            dom, matcap_mean, matcap_parts = None, None, []
            if g['matcap_mode'] and max(g['tint']) >= args.matcap_min_tint:
                dom = pick_matcap(g)
                matcap_mean, matcap_parts = matcap_average(g, packs, tmpdir, 'mc_' + stem)

            dec = dict(group=stem, base=base, slots=g['slots'], triangles=g['tris'],
                       alphaWanted=bool(g['alpha_wanted']),
                       alphaWhy=g['why_alpha'][:4], hasAlpha=bool(has_alpha),
                       template='alphaclip' if use_alphaclip else 'advanced',
                       matcapMode=g['matcap_mode'],
                       matcapTint=[round(x, 4) for x in g['tint']],
                       matcapDominant=dom is not None,
                       matcapTex=(dom['tex'] if dom else None),
                       matcapParts=matcap_parts,
                       matcapAvgRgb=([round(float(x), 1) for x in matcap_mean]
                                     if matcap_mean is not None else None),
                       material='0x%016x' % mat_id,
                       colorId='0x%016x' % cur.tex_ids[ADV_COLOR],
                       note=note_alpha)
            decisions.append((base, stem, g, cur, arr, vtf, alpha, dec, dom, matcap_mean))

            note = note_alpha
            if g['matcaps']:
                note = ('matcap %s -> mean RGB %s | candidates: %s'
                        % (os.path.basename(dom['tex']) if dom else '-',
                           matcap_mean.round(1).tolist() if matcap_mean is not None else '-',
                           ', '.join('%s(tint %.2f, %d tris, %d slots)'
                                     % (os.path.basename(e['tex']), max(e['tint']),
                                        e['tris'], len(e['slots']))
                                     for e in g['matcaps'].values())))
            log('%-34s %5d %5d  %-9s %-6s  %s'
                % (stem, len(g['slots']), g['tris'], dec['template'],
                   (os.path.basename(dom['tex']) if dom else '-'), note))
        log('-' * 100)
        n_ac = sum(1 for d in decisions if d[7]['template'] == 'alphaclip')
        n_dom = sum(1 for d in decisions if d[7]['matcapDominant'])
        log('  %d groups: %d alphaclip (transparency), %d matcap-driven (metal), %d untouched'
            % (len(decisions), n_ac, n_dom,
               len(decisions) - n_ac - n_dom))

        if args.report_only:
            log('  report only: not writing')
            return 0

        # ---- build the new textures --------------------------------------
        new_tex = []
        for base, stem, g, cur, arr, vtf, alpha, dec, dom, matcap_mean in decisions:
            # transparency mask
            if dec['template'] == 'alphaclip':
                tid = make_id(TEX_ID_PREFIX, 'tex/mask/' + stem)
                if tid in tex_entries:
                    raise AssertionError('mask id %016x already shipped' % tid)
                rgb8 = np.dstack([alpha, alpha, alpha]).astype(np.uint8)
                a = np.full(alpha.shape + (1,), 255, np.uint8)
                t, px = encode_array(np.concatenate([rgb8, a], axis=2),
                                     TP.DXGI_BC7_UNORM, tmpdir, 'mask_' + stem)
                dec['maskId'] = '0x%016x' % tid
                dec['maskSize'] = '%dx%d' % (vtf.width, vtf.height)
                dec['maskFormat'] = vtf.format_name
                dec['cutAtThresholdPct'] = round(100.0 * float((alpha < 128).mean()), 2)
                new_tex.append((tid, t, px, 'texture:mask:%s' % stem))
                # preview: source RGB | alpha | binary at 0.5
                from PIL import Image
                s = 512
                binv = np.where(alpha >= 128, 255, 0).astype(np.uint8)
                tiles = [Image.fromarray(arr[:, :, :3], 'RGB').resize((s, s), Image.LANCZOS),
                         Image.fromarray(np.dstack([alpha.astype(np.uint8)] * 3), 'RGB').resize((s, s), Image.LANCZOS),
                         Image.fromarray(np.dstack([binv] * 3), 'RGB').resize((s, s), Image.NEAREST)]
                sheet = Image.new('RGB', (s * 3, s))
                for i, tile in enumerate(tiles):
                    sheet.paste(tile, (i * s, 0))
                sheet.save(os.path.join(args.out, 'preview', 'mask_%s.png' % stem))

            # matcap-driven metal
            if dec['matcapDominant']:
                base_arr, _bt = decode_patch_texture(args.src, tex_entries[cur.tex_ids[ADV_COLOR]],
                                                     tmpdir, 'col_' + stem)
                lift = (matcap_mean * np.asarray(g['tint'], np.float32)).round().astype(np.int32)
                alb = np.clip(base_arr[:, :, :3].astype(np.int32) + lift, 0, 255).astype(np.uint8)
                alb = np.concatenate([alb, np.zeros(alb.shape[:2] + (1,), np.uint8)], axis=2)
                tid = make_id(TEX_ID_PREFIX, 'tex/albedo/' + stem)
                if tid in tex_entries:
                    raise AssertionError('albedo id %016x already shipped' % tid)
                t, px = encode_array(alb, TP.DXGI_BC7_SRGB, tmpdir, 'alb_' + stem)
                dec['albedoId'] = '0x%016x' % tid
                dec['albedoFrom'] = dec['colorId']
                dec['albedoLift'] = [int(x) for x in lift]
                dec['albedoSize'] = '%dx%d' % (alb.shape[1], alb.shape[0])
                new_tex.append((tid, t, px, 'texture:albedo:%s' % stem))

                tid = make_id(TEX_ID_PREFIX, 'tex/rough/' + stem)
                if tid in tex_entries:
                    raise AssertionError('roughness id %016x already shipped' % tid)
                nr = np.dstack([np.full(base_arr.shape[:2], FLAT_NORMAL_RGB[0], np.uint8),
                                np.full(base_arr.shape[:2], FLAT_NORMAL_RGB[1], np.uint8),
                                np.full(base_arr.shape[:2], FLAT_NORMAL_RGB[2], np.uint8),
                                np.full(base_arr.shape[:2], args.roughness, np.uint8)])
                t, px = encode_array(nr, TP.DXGI_BC7_UNORM, tmpdir, 'rgh_' + stem)
                dec['normalRoughId'] = '0x%016x' % tid
                dec['roughness'] = args.roughness
                new_tex.append((tid, t, px, 'texture:normalrough:%s' % stem))

                tid = make_id(TEX_ID_PREFIX, 'tex/metal/' + stem)
                if tid in tex_entries:
                    raise AssertionError('metallic id %016x already shipped' % tid)
                mt = np.dstack([np.full(base_arr.shape[:2], args.metallic, np.uint8),
                                np.full(base_arr.shape[:2], args.metallic, np.uint8),
                                np.full(base_arr.shape[:2], args.metallic, np.uint8),
                                np.full(base_arr.shape[:2], 255, np.uint8)])
                t, px = encode_array(mt, TP.DXGI_BC7_UNORM, tmpdir, 'met_' + stem)
                dec['metallicId'] = '0x%016x' % tid
                dec['metallic'] = args.metallic
                new_tex.append((tid, t, px, 'texture:metallic:%s' % stem))

                # preview: old albedo | matcap | new albedo
                from PIL import Image
                s = 512
                sheet = Image.new('RGB', (s * 3, s))
                sheet.paste(Image.fromarray(base_arr[:, :, :3], 'RGB').resize((s, s), Image.NEAREST), (0, 0))
                sheet.paste(Image.fromarray(matcap_mean.astype(np.uint8).reshape(1, 1, 3)
                                            .repeat(s, 0).repeat(s, 1), 'RGB'), (s, 0))
                sheet.paste(Image.fromarray(alb[:, :, :3], 'RGB').resize((s, s), Image.NEAREST), (2 * s, 0))
                sheet.save(os.path.join(args.out, 'preview', 'metal_%s.png' % stem))

        new_tex_entries = [PW.Entry(A.TEXTURE_TYPE_ID, tid, t.write_toc(), b'', px,
                                    unknown1=0, unknown2=0, note=note)
                           for tid, t, px, note in new_tex]
    finally:
        shutil.rmtree(tmpdir, ignore_errors=True)

    # ---- rebuild the container -------------------------------------------
    tpl_bytes = {k: open(os.path.join(MATERIALS_DIR, f), 'rb').read()
                 for k, f in TEMPLATE_FILE.items()}
    by_material = {d['material']: (base, stem, g, cur, arr, vtf, alpha, d, dom, mm)
                   for base, stem, g, cur, arr, vtf, alpha, d, dom, mm in decisions}
    out_entries, replaced, seen = [], set(), {'alphaclip': 0, 'advanced': 0}
    for e in entries:
        pl = HP.entry_payload(e, args.src)
        dec = (by_material.get('0x%016x' % e['file_id'])
               if e['type_id'] == A.MATERIAL_TYPE_ID else None)
        if dec is None or (dec[7]['template'] == 'advanced'
                           and not dec[7]['matcapDominant']):
            # untouched group: not a material type, not alphaclip, and no
            # matcap to bake -- emit it verbatim, byte for byte
            out_entries.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'], pl['stream'],
                                        pl['gpu'], e['unknown1'], e['unknown2'],
                                        e['unknown3'], e['unknown4'], note='verbatim'))
            continue
        base, stem, g, cur, arr, vtf, alpha, d, dom, mm = dec
        m = A.Material.read(tpl_bytes[d['template']])
        if d['template'] == 'alphaclip':
            m.set_texture(AC_NORMAL, cur.tex_ids[ADV_NORMAL])
            m.set_texture(AC_MASK, int(d['maskId'], 16))
            m.set_texture(AC_COLOR, cur.tex_ids[ADV_COLOR])
        else:
            # advanced: normal/roughness + metallic + albedo all become the
            # group's own maps, because the source had no albedo of its own
            m.set_texture(ADV_NORMAL, int(d['normalRoughId'], 16))
            m.set_texture(ADV_METALLIC, int(d['metallicId'], 16))
            m.set_texture(ADV_COLOR, int(d['albedoId'], 16))
        seen[d['template']] += 1
        out_entries.append(PW.Entry(e['type_id'], e['file_id'], m.write(), pl['stream'],
                                    pl['gpu'], e['unknown1'], e['unknown2'],
                                    e['unknown3'], e['unknown4'],
                                    note='material:%s' % d['template']))
        replaced.add(e['index'])
    out_entries.extend(new_tex_entries)
    log('  rebuilt %d alphaclip + %d advanced, added %d textures'
        % (seen['alphaclip'], seen['advanced'], len(new_tex_entries)))
    want_rebuilt = sum(1 for d in decisions
                       if d[7]['template'] == 'alphaclip' or d[7]['matcapDominant'])
    if seen['alphaclip'] + seen['advanced'] != want_rebuilt:
        raise AssertionError('rebuilt %d materials, expected %d'
                             % (seen['alphaclip'] + seen['advanced'], want_rebuilt))

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
        if HP.entry_payload(a, args.src) != HP.entry_payload(b, res['toc']):
            raise AssertionError('verbatim entry 0x%016x changed' % a['file_id'])
        if a['gpu_offset'] != b['gpu_offset']:
            raise AssertionError('gpu offset of 0x%016x moved' % a['file_id'])
    if res['gpu_size'] < os.path.getsize(args.src + '.gpu_resources'):
        raise AssertionError('gpu blob shrank')

    for base, stem, g, cur, arr, vtf, alpha, d, dom, mm in decisions:
        if d['template'] == 'advanced' and not d['matcapDominant']:
            continue                       # untouched: covered by the verbatim sweep
        e = by_key[(A.MATERIAL_TYPE_ID, int(d['material'], 16))]
        m = A.Material.read(HP.entry_payload(e, res['toc'])['toc'])
        if m.parent_name != d['template']:
            raise AssertionError('%s came back as %s' % (stem, m.parent_name))
        if d['template'] == 'alphaclip':
            want = [cur.tex_ids[ADV_NORMAL], int(d['maskId'], 16), cur.tex_ids[ADV_COLOR]]
            got = m.tex_ids
        else:
            want = [int(d['normalRoughId'], 16), int(d['metallicId'], 16),
                    int(d['albedoId'], 16)]
            got = [m.tex_ids[ADV_NORMAL], m.tex_ids[ADV_METALLIC], m.tex_ids[ADV_COLOR]]
        if got != want:
            raise AssertionError('%s %s slots are wrong: %s != %s'
                                 % (stem, d['template'], [hex(x) for x in got],
                                    [hex(x) for x in want]))

    # masks must decode back to the source alpha; albedo to the expected lift
    tmp2 = tempfile.mkdtemp(prefix='appearancecheck')
    try:
        for base, stem, g, cur, arr, vtf, alpha, d, dom, mm in decisions:
            if d['template'] == 'alphaclip':
                e = by_key[(A.TEXTURE_TYPE_ID, int(d['maskId'], 16))]
                got, _t = decode_patch_texture(res['toc'], e, tmp2, 'v_' + stem)
                dd = np.abs(got[:, :, 0].astype(np.int32) - alpha)
                log('  verify mask   %-30s max|d|=%3d mean|d|=%.3f'
                    % (stem, int(dd.max()), float(dd.mean())))
                if dd.mean() > 4.0:
                    raise AssertionError('mask for %s does not match the source alpha' % stem)
            if d['matcapDominant']:
                e = by_key[(A.TEXTURE_TYPE_ID, int(d['albedoId'], 16))]
                got, _t = decode_patch_texture(res['toc'], e, tmp2, 'va_' + stem)
                if arr is None:
                    # The source base colour is an engine built-in (`black`) or
                    # otherwise undecodable; there is no "expected lift" to
                    # compare against, so just check the emission mask is clear.
                    log('  verify albedo %-30s skipped (source base colour '
                        'undecodable; built-in?)' % stem)
                    if got[:, :, 3].max() > 8:
                        raise AssertionError('albedo for %s kept a non-zero emission mask' % stem)
                    continue
                want = np.clip(arr[:, :, :3].astype(np.int32) + np.asarray(d['albedoLift'], np.int32),
                               0, 255).astype(np.int32)
                dd = np.abs(got[:, :, :3].astype(np.int32) - want)
                log('  verify albedo %-30s max|d|=%3d mean|d|=%.3f  mean RGB %s'
                    % (stem, int(dd.max()), float(dd.mean()),
                       got[:, :, :3].reshape(-1, 3).mean(axis=0).round(1).tolist()))
                if dd.mean() > 6.0:
                    raise AssertionError('albedo for %s is not the expected lift' % stem)
                if got[:, :, 3].max() > 8:
                    raise AssertionError('albedo for %s kept a non-zero emission mask' % stem)
    finally:
        shutil.rmtree(tmp2, ignore_errors=True)

    meta = dict(schema='hd2mod.rig-lab-appearance', sourcePatch=args.src,
                knobs=dict(matcapMinTint=args.matcap_min_tint,
                           metallic=args.metallic, roughness=args.roughness),
                advancedSlots=dict(normal=ADV_NORMAL, metallic=ADV_METALLIC,
                                   color=ADV_COLOR),
                alphaclipSlots=dict(normal=AC_NORMAL, mask=AC_MASK, color=AC_COLOR),
                groups=[d for *_x, d, _dom, _mm in decisions],
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
