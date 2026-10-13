# -*- coding: utf-8 -*-
"""One HD2 patch per L4D2 material VPK of the 【SHOP】Lunalice八人包 archive.

The shipping main patch (M8) already carries the *default* look
(A1主体 + B1服装色白 + C2金属装饰品色银 + D1发色白 + E3瞳色紫2 + F1身体描边).
This tool re-runs the **same** data-driven appearance pass for every material
VPK, with that VPK's VMT/VTF layered on top of the default packs, and writes a
standalone patch that contains **only the entries whose content changed**.

HD2 resolves same-asset-key collisions by load order (a higher patch index
wins), so dropping one of these next to the main patch switches that family's
look -- that is the whole point.

Nothing here is hard-coded to a region or a material id: the target groups, the
texture ids and the template decisions all come out of the model's own 92-slot
table plus the source packs, exactly like `fix_appearance.py`.

usage:
  split_materials.py [--m8 <dir>] [--out <dir>] [--only B2,C1] [--dry-run]
                     [--skip-identical] [--no-verify]
"""
import argparse
import copy
import hashlib
import json
import os
import shutil
import sys
import tempfile
from collections import OrderedDict

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import export_obj as E
import fix_materials as FM
import hd2_asset as A
import hd2_patch as HP
import hd2_patchw as PW
import hd2_source as S
import hd2_style as ST
import hd2_texpack as TP
import fix_appearance as FA

PKG8 = r'E:\tmp\pkg8'
M8_DIR = r'E:\mod\Lunalice-RE2310-M8'
OUT_ROOT = r'E:\mod'
PATCH_NAME = '9ba626afa44a3aa3.patch_0'
FOLDER_PREFIX = 'Lunalice-RE2310-MAT'

# the archive's own 基础材质 set -- tag, file name, human label
TARGETS = [
    ('A1', 'Lunalice基础材质A1主体.vpk',         '基础主体'),
    ('B1', 'Lunalice基础材质B1服装色白.vpk',      '服装色白'),
    ('B2', 'Lunalice基础材质B2服装色黑.vpk',      '服装色黑'),
    ('C1', 'Lunalice基础材质C1金属装饰品色金.vpk', '金属装饰品色金'),
    ('C2', 'Lunalice基础材质C2金属装饰品色银.vpk', '金属装饰品色银'),
    ('D1', 'Lunalice基础材质D1发色白.vpk',        '发色白'),
    ('D2', 'Lunalice基础材质D2发色紫.vpk',        '发色紫'),
    ('D3', 'Lunalice基础材质D3发色浅灰.vpk',      '发色浅灰'),
    ('D4', 'Lunalice基础材质D4发色黄.vpk',        '发色黄'),
    ('E1', 'Lunalice基础材质E1瞳色紫1.vpk',       '瞳色紫1'),
    ('E2', 'Lunalice基础材质E2瞳色红.vpk',        '瞳色红'),
    ('E3', 'Lunalice基础材质E3瞳色紫2.vpk',       '瞳色紫2'),
    ('F1', 'Lunalice基础材质F1身体描边.vpk',      '身体描边'),
    ('F2', 'Lunalice基础材质F2身体描边无.vpk',    '身体描边无'),
]

# what the main patch was built from -- the "default" the variants override
DEFAULT_PACKS = [S.MODEL_VPK] + list(S.PACK_VPKS[1:])


def log(m=''):
    print(m, flush=True)


# --------------------------------------------------------------------- packs
def packs_with(variant_path, base):
    """A shallow copy of `base` with `variant_path`'s VMT/VTF layered on top.

    -> (packs, changed stems).  `changed` holds the basenames whose bytes really
    differ from what the base packs already resolve -- if that set is empty the
    VPK cannot change anything, and if a group touches none of it the group's
    output cannot change either.  (Exact: a group's output is a pure function of
    its VMTs, its base VTF and its matcap VTFs.)
    """
    p = copy.copy(base)
    p.vmt = dict(base.vmt)
    p.vtf = dict(base.vtf)
    p._vmt_short = dict(base._vmt_short)
    p._vtf_short = dict(base._vtf_short)
    _v, do, ents = E.parse_vpk_tree(variant_path)
    changed = set()
    n = 0
    for f, a, o, l in ents:
        fl = f.replace('/', '\\').lower()
        b = os.path.basename(fl)
        if fl.endswith('.vmt'):
            blob = E.read_range(variant_path, do, a, o, l)
            if blob is None:
                continue
            old = base.vmt.get(base._vmt_short.get(b[:-4], ''))
            if old != blob:
                changed.add(b[:-4])
            p.vmt[fl] = blob
            p._vmt_short[b[:-4]] = fl
            n += 1
        elif fl.endswith('.vtf'):
            blob = E.read_range(variant_path, do, a, o, l)
            if blob is None:
                continue
            old = base.vtf.get(base._vtf_short.get(b[:-4], ''))
            if old != blob:
                changed.add(b[:-4])
            p.vtf[fl] = blob
            p._vtf_short[b[:-4]] = fl
            n += 1
    return p, n, changed


# ------------------------------------------------------------------ textures
def decode_asset(t, tmpdir, tag):
    """A built Texture -> RGBA array (round trip through the same BC7 codec)."""
    from PIL import Image
    dds = os.path.join(tmpdir, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(t.to_dds())
    FA._run([FA.TEXCONV, '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
             '-m', '1', '-o', tmpdir, dds])
    png = os.path.join(tmpdir, tag + '.png')
    return np.asarray(Image.open(png).convert('RGBA'), np.uint8)


def group_assets(stem, g, packs, m8g, tmpdir, knobs, m8_patch, m8_tex):
    """Everything one group needs, computed the way `fix_appearance` does.

    -> dict(decision=..., textures={kind: (tex_id, Texture, pixels, note)},
            material=(bytes | None))
    """
    dec = dict(group=stem, alphaWanted=bool(g['alpha_wanted']),
               matcapMode=g['matcap_mode'],
               matcapTint=[round(x, 4) for x in g['tint']],
               m8Template=m8g['template'], m8MatcapDominant=m8g['matcapDominant'])

    arr, vtf = FA.decode_vtf(packs, stem, tmpdir, 'g_' + stem)
    alpha, has_alpha = None, False
    if arr is not None:
        alpha = arr[:, :, 3].astype(np.int32)
        spread = float(alpha.max() - alpha.min())
        cover = float((alpha < 250).mean())
        has_alpha = spread >= FA.ALPHA_MIN_SPREAD and cover >= FA.ALPHA_MIN_COVERAGE
        dec['alphaSpread'] = round(spread, 1)
        dec['alphaCoverage'] = round(cover, 5)
    dec['hasAlpha'] = bool(has_alpha)
    use_ac = bool(g['alpha_wanted'] and has_alpha)
    dec['template'] = 'alphaclip' if use_ac else 'advanced'

    dom = bool(g['matcap_mode'] and max(g['tint']) >= knobs['matcapMinTint'])
    dec['matcapDominant'] = dom

    out = {}
    color_id = FA.make_id(FA.TEX_ID_PREFIX, 'tex/base/' + stem)

    # ---- the base colour ------------------------------------------------
    raw = packs.vtf_bytes(stem)
    if raw is None:
        dec['note'] = 'no source VTF for %r' % stem
    else:
        t, px = TP.build_texture(raw, TP.DXGI_BC7_SRGB, color_id,
                                 tmpdir=tmpdir, tag='base_' + stem)
        # the main patch's base colours went through M6's alpha-zero pass (the
        # reference mod's slot-5 alpha is 0), so a variant must too or every
        # patch would silently re-introduce an emission mask
        px = FM.zero_alpha(t.dds_header, px, tmpdir, 'za_' + stem)
        t.pixels = px
        out['base'] = (color_id, t, px, 'texture:base:' + stem)

    # ---- the alpha mask --------------------------------------------------
    if use_ac and alpha is not None:
        mid = FA.make_id(FA.TEX_ID_PREFIX, 'tex/mask/' + stem)
        rgb8 = np.dstack([alpha, alpha, alpha]).astype(np.uint8)
        a8 = np.full(alpha.shape + (1,), 255, np.uint8)
        t, px = FA.encode_array(np.concatenate([rgb8, a8], axis=2),
                                TP.DXGI_BC7_UNORM, tmpdir, 'mask_' + stem)
        out['mask'] = (mid, t, px, 'texture:mask:' + stem)
        dec['cutAtThresholdPct'] = round(100.0 * float((alpha < 128).mean()), 2)

    # ---- the matcap-baked albedo ----------------------------------------
    if dom:
        aid = FA.make_id(FA.TEX_ID_PREFIX, 'tex/albedo/' + stem)
        mean, parts = FA.matcap_average(g, packs, tmpdir, 'mc_' + stem)
        dec['matcapAvgRgb'] = [round(float(x), 1) for x in mean]
        dec['matcapParts'] = parts
        base_arr = None
        e = m8_tex.get(color_id)
        if e is not None:
            base_arr, _t = FA.decode_patch_texture(m8_patch, e, tmpdir, 'col_' + stem)
        if base_arr is None:
            base_arr = arr
        lift = (mean * np.asarray(g['tint'], np.float32)).round().astype(np.int32)
        alb = np.clip(base_arr[:, :, :3].astype(np.int32) + lift, 0, 255).astype(np.uint8)
        alb = np.concatenate([alb, np.zeros(alb.shape[:2] + (1,), np.uint8)], axis=2)
        t, px = FA.encode_array(alb, TP.DXGI_BC7_SRGB, tmpdir, 'alb_' + stem)
        out['albedo'] = (aid, t, px, 'texture:albedo:' + stem)
        dec['albedoLift'] = [int(x) for x in lift]

    # ---- the material, only when the template decision moved -------------
    mat = None
    mid = FA.make_id(FA.MAT_ID_PREFIX, 'mat/' + (g['base'] or 'untextured'))
    dec['materialId'] = '0x%016x' % mid
    if dec['template'] != m8g['template']:
        dec['materialReason'] = ('template %s -> %s'
                                 % (m8g['template'], dec['template']))
        mat = (mid, dec['template'])
    return dec, out, mat


def emit_material(tpl_bytes, template, stem, cur_ids):
    m = A.Material.read(tpl_bytes[template])
    if template == 'alphaclip':
        m.set_texture(FA.AC_NORMAL, cur_ids['normal'])
        m.set_texture(FA.AC_MASK, cur_ids['mask'])
        m.set_texture(FA.AC_COLOR, cur_ids['color'])
    else:
        m.set_texture(FA.ADV_NORMAL, cur_ids['normal'])
        m.set_texture(FA.ADV_METALLIC, cur_ids['metallic'])
        m.set_texture(FA.ADV_COLOR, cur_ids['color'])
    return m.write()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--m8', default=M8_DIR)
    ap.add_argument('--out', default=OUT_ROOT)
    ap.add_argument('--only', default=None,
                    help='comma separated tags, e.g. B2,C1')
    ap.add_argument('--dry-run', action='store_true')
    ap.add_argument('--skip-identical', action='store_true',
                    help='do not write a patch whose content equals the main patch')
    ap.add_argument('--no-verify', action='store_true')
    args = ap.parse_args()

    src_patch = os.path.join(args.m8, PATCH_NAME)
    entries, _raw = HP.scan_entries(src_patch)
    m8_tex = {e['file_id']: e for e in entries if e['type_id'] == A.TEXTURE_TYPE_ID}
    m8_mat = {e['file_id']: e for e in entries if e['type_id'] == A.MATERIAL_TYPE_ID}
    man = json.load(open(os.path.join(args.m8, 'lunalice-m8.manifest.json'),
                         encoding='utf-8'))
    m8g = {d['group']: d for d in man['groups']}
    knobs = man['knobs']
    log('=' * 110)
    log('split_materials -- main patch %s' % src_patch)
    log('  %d entries (%d textures, %d materials), %d groups, knobs %s'
        % (len(entries), len(m8_tex), len(m8_mat), len(m8g), knobs))

    # ---- the model's own slot table ---------------------------------------
    vpk = S.ADDONS + S.MODEL_VPK
    _md, mdl, _v, tris4, _s, _sh = ST.load_source(vpk, 'survivor_teenangst')
    tris4 = np.asarray(tris4, np.int64)
    names, cds, _row = S.load_material_slots(mdl)
    base_packs = S.Packs()
    log('  model: cdtextures %s' % cds)

    tpl_bytes = {k: open(os.path.join(FA.MATERIALS_DIR, f), 'rb').read()
                 for k, f in FA.TEMPLATE_FILE.items()}

    # ---- built-in refutation: the base packs must reproduce M8 exactly -----
    tmpdir = tempfile.mkdtemp(prefix='splitmat')
    try:
        groups0 = FA.analyse(tris4, names, base_packs)
        same_all, worst = True, (0.0, 0)
        for base, g in groups0.items():
            stem = os.path.basename(base or 'untextured')
            if stem not in m8g:
                log('  !! group %s is not in the M8 manifest' % stem)
                same_all = False
                continue
            dec, out, mat = group_assets(stem, g, base_packs, m8g[stem], tmpdir,
                                         knobs, src_patch, m8_tex)
            for kind, (tid, t, px, _n) in out.items():
                e = m8_tex.get(tid)
                if e is None:
                    log('  !! %s/%s id %016x absent from M8' % (stem, kind, tid))
                    same_all = False
                    continue
                pl = HP.entry_payload(e, src_patch)
                if pl['gpu'] == px:
                    continue
                got = decode_asset(t, tmpdir, 'chk_%s_%s' % (stem, kind))
                ref = FA.decode_patch_texture(src_patch, e, tmpdir,
                                              'ref_%s_%s' % (stem, kind))[0]
                d = np.abs(got.astype(np.int32) - ref.astype(np.int32))
                worst = (max(worst[0], float(d.mean())), max(worst[1], int(d.max())))
                if d.max() > 8 or d.mean() > 0.5:
                    log('  !! refutation: %s/%s differs from M8 (mean %.3f max %d)'
                        % (stem, kind, d.mean(), d.max()))
                    same_all = False
        log('  built-in refutation (base packs == M8): %s   worst mean|d|=%.3f max=%d'
            % ('PASS' if same_all else 'FAIL', worst[0], worst[1]))
        if not args.no_verify and not same_all:
            raise AssertionError('the base packs do not reproduce M8 -- stop')
    finally:
        shutil.rmtree(tmpdir, ignore_errors=True)

    # ---- one patch per material VPK ---------------------------------------
    wanted = set(args.only.split(',')) if args.only else None
    results = []
    for tag, fname, label in TARGETS:
        if wanted and tag not in wanted:
            continue
        path = os.path.join(PKG8, fname)
        if not os.path.exists(path):
            log('  [skip] %s -- missing' % fname)
            continue
        log('')
        log('=' * 110)
        log('%s  %s  (%s, %d B)' % (tag, label, fname, os.path.getsize(path)))

        packs, nsrc, changed_stems = packs_with(path, base_packs)
        groups = FA.analyse(tris4, names, packs)
        touched_slots = {i for i in range(len(names)) if names[i].lower() in changed_stems}
        scope = set()
        for base, g in groups.items():
            stem = os.path.basename(base or 'untextured')
            if stem not in m8g:
                continue
            if (stem in changed_stems
                    or any(os.path.basename(t) in changed_stems for t in g['matcaps'])
                    or any(i in touched_slots for i in g['slots'])):
                scope.add(stem)
        log('  layered %d vmt/vtf files; %d differ from the default packs -> %d of '
            '%d groups can change' % (nsrc, len(changed_stems), len(scope), len(m8g)))

        tmpdir = tempfile.mkdtemp(prefix='split_' + tag)
        out_tex, out_mat, report = [], [], []
        try:
            for base, g in groups.items():
                stem = os.path.basename(base or 'untextured')
                if stem not in m8g:
                    continue
                if stem not in scope:
                    report.append(dict(group=stem, skipped='no input changed',
                                       template=m8g[stem]['template'], changed=[],
                                       textures=[]))
                    continue
                dec, out, mat = group_assets(stem, g, packs, m8g[stem], tmpdir,
                                             knobs, src_patch, m8_tex)
                changed, rows = [], []
                for kind, (tid, t, px, note) in out.items():
                    e = m8_tex.get(tid)
                    if e is None:
                        rows.append('%s:NEW' % kind)
                        changed.append(kind)
                        out_tex.append((tid, t, px, note))
                        continue
                    pl = HP.entry_payload(e, src_patch)
                    if pl['gpu'] == px:
                        rows.append('%s:same' % kind)
                        continue
                    rows.append('%s:DIFF' % kind)
                    changed.append(kind)
                    out_tex.append((tid, t, px, note))
                mat_bytes = None
                if mat is not None:
                    mid, template = mat
                    cur = m8_mat[mid]
                    curpl = HP.entry_payload(cur, src_patch)
                    curm = A.Material.read(curpl['toc'])
                    if dec['template'] == 'alphaclip':
                        cids = dict(normal=curm.tex_ids[FA.AC_NORMAL],
                                    mask=FA.make_id(FA.TEX_ID_PREFIX, 'tex/mask/' + stem),
                                    color=curm.tex_ids[FA.AC_COLOR],
                                    metallic=curm.tex_ids[FA.ADV_METALLIC]
                                    if len(curm.tex_ids) > FA.ADV_METALLIC else 0)
                    else:
                        cids = dict(normal=curm.tex_ids[0],
                                    mask=0,
                                    color=FA.make_id(FA.TEX_ID_PREFIX, 'tex/base/' + stem),
                                    metallic=FA.make_id(FA.TEX_ID_PREFIX,
                                                        'tex/metal/' + stem))
                    mat_bytes = (mid, emit_material(tpl_bytes, dec['template'],
                                                    stem, cids), cur)
                    changed.append('material')
                report.append(dict(dec, textures=rows, changed=changed))
                if changed:
                    log('   %-32s %-9s %s' % (stem, dec['template'],
                                              ' '.join(rows) + (' +material'
                                                                if mat is not None else '')))
                if mat_bytes is not None:
                    out_mat.append(mat_bytes)
        finally:
            shutil.rmtree(tmpdir, ignore_errors=True)

        n_diff = len([r for r in report if r['changed']])
        log('  -> %d of %d groups differ; %d textures, %d materials'
            % (n_diff, len(report), len(out_tex), len(out_mat)))

        folder = os.path.join(args.out, '%s-%s%s' % (FOLDER_PREFIX, tag, label))
        meta = dict(schema='hd2mod.rig-lab-material-split', sourceVpk=fname,
                    sourceVpkBytes=os.path.getsize(path), tag=tag, label=label,
                    mainPatch=src_patch, knobs=knobs,
                    groupsTouched=len([r for r in report if r['changed']]),
                    groupsTotal=len(report),
                    identicalToMainPatch=(n_diff == 0),
                    groups=report)
        results.append((tag, label, folder, n_diff, out_tex, out_mat, meta, report))

    log('')
    log('=' * 110)
    log('%-4s %-16s %8s %8s %8s  %s' % ('tag', 'label', 'groups', 'tex', 'mat', 'folder'))
    log('-' * 110)
    for tag, label, folder, n_diff, out_tex, out_mat, meta, report in results:
        log('%-4s %-16s %8d %8d %8d  %s'
            % (tag, label, n_diff, len(out_tex), len(out_mat), os.path.basename(folder)))
    if args.dry_run:
        log('')
        log('dry run: nothing written')
        return 0

    for tag, label, folder, n_diff, out_tex, out_mat, meta, report in results:
        if n_diff == 0 and args.skip_identical:
            log('[skip] %s %s -- identical to the main patch' % (tag, label))
            continue
        ent = []
        for tid, t, px, note in out_tex:
            src = m8_tex.get(tid)
            if src is not None:
                pl = HP.entry_payload(src, src_patch)
                ent.append(PW.Entry(A.TEXTURE_TYPE_ID, tid, t.write_toc(),
                                    pl['stream'], px, src['unknown1'], src['unknown2'],
                                    src['unknown3'], src['unknown4'], note=note))
            else:
                ent.append(PW.Entry(A.TEXTURE_TYPE_ID, tid, t.write_toc(), b'', px,
                                    unknown1=0, unknown2=0, note=note))
        for mid, mb, cur in out_mat:
            pl = HP.entry_payload(cur, src_patch)
            ent.append(PW.Entry(A.MATERIAL_TYPE_ID, mid, mb, pl['stream'], pl['gpu'],
                                cur['unknown1'], cur['unknown2'], cur['unknown3'],
                                cur['unknown4'], note='material'))
        if not ent:
            log('[skip] %s %s -- nothing to write' % (tag, label))
            continue
        os.makedirs(folder, exist_ok=True)
        res = PW.write_patch(ent, src_patch, folder, PATCH_NAME)
        meta['outputPatch'] = res['toc']
        meta['tocBytes'] = res['toc_size']
        meta['gpuBytes'] = res['gpu_size']
        meta['entryCount'] = len(ent)
        meta['tocSha256'] = hashlib.sha256(open(res['toc'], 'rb').read()).hexdigest()
        meta['gpuSha256'] = hashlib.sha256(open(res['gpu'], 'rb').read()).hexdigest()
        with open(os.path.join(folder, '%s-%s.manifest.json' % (FOLDER_PREFIX, tag)),
                  'w', encoding='utf-8') as f:
            json.dump(meta, f, indent=2, ensure_ascii=False)

        # ---- read back -----------------------------------------------------
        back, _r = HP.scan_entries(res['toc'])
        if len(back) != len(ent):
            raise AssertionError('%s: %d entries written, %d read back'
                                 % (tag, len(back), len(ent)))
        bk = {(e['type_id'], e['file_id']): e for e in back}
        for tid, t, px, _n in out_tex:
            e = bk[(A.TEXTURE_TYPE_ID, tid)]
            if HP.entry_payload(e, res['toc'])['gpu'] != px:
                raise AssertionError('%s: texture %016x did not round trip' % (tag, tid))
        for mid, mb, _cur in out_mat:
            e = bk[(A.MATERIAL_TYPE_ID, mid)]
            if HP.entry_payload(e, res['toc'])['toc'] != mb:
                raise AssertionError('%s: material %016x did not round trip' % (tag, mid))
        log('[ok] %-4s %-16s %2d entries  toc %d B  gpu %d B  sha256 %s'
            % (tag, label, len(ent), res['toc_size'], res['gpu_size'],
               meta['tocSha256'][:16]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
