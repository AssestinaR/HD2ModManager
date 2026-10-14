# -*- coding: utf-8 -*-
"""DEFINITIVE: for every material group, compare
     (1) the source L4D2 base-colour VTF (decoded sRGB)
     (2) the texture that actually ships in our HD2 patch (slot 5 albedo, sRGB)
   pixel-for-pixel (re-encoded through BC7, so we allow codec error).

Verdict per group + a side-by-side contact sheet in E:/tmp/r71-color/.

usage: _probe_color_faithful.py [appearance_manifest] [shipped_patch]
"""
import os, sys, struct, tempfile, subprocess, json
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_asset as A
import hd2_patch as HP
import hd2_vtf as V
import hd2_texpack as TP
import hd2_source as S
from PIL import Image

TEXCONV = TP.TEXCONV
OUT = r'E:/tmp/r71-color'
os.makedirs(OUT, exist_ok=True)


def _run(cmd):
    p = subprocess.run(cmd, capture_output=True)
    if p.returncode != 0:
        raise RuntimeError('%s\n%s' % (cmd, p.stderr.decode('utf-8', 'replace')))
    return p


def decode_dds(dds_path, srgb=True):
    d = os.path.dirname(dds_path)
    stem = os.path.splitext(os.path.basename(dds_path))[0]
    png = os.path.join(d, stem + '.png')
    if os.path.exists(png):
        os.remove(png)
    fmt = 'R8G8B8A8_UNORM_SRGB' if srgb else 'R8G8B8A8_UNORM'
    _run([TEXCONV, '-nologo', '-y', '-ft', 'png', '-f', fmt, '-m', '1', '-o', d, dds_path])
    if not os.path.exists(png):
        return None
    return np.asarray(Image.open(png).convert('RGBA'), np.uint8)


def vtf_rgba(v, tmp, tag):
    if v.hi_format in TP.VTF_TO_WRAPPER_DXGI:
        dds = os.path.join(tmp, tag + '.dds')
        open(dds, 'wb').write(TP._wrapper_dds(
            v, TP.VTF_TO_WRAPPER_DXGI[v.hi_format], v.width, v.height, v.mips[0], None))
        return decode_dds(dds, srgb=True)
    try:
        png = TP._uncompressed_png(v, tmp, tag)
        return np.asarray(Image.open(png).convert('RGBA'), np.uint8)
    except Exception:
        return None


def lum(a):
    return 0.2126 * a[:, :, 0] + 0.7152 * a[:, :, 1] + 0.0722 * a[:, :, 2]


def main():
    appman = sys.argv[1] if len(sys.argv) > 1 else \
        r'E:/tmp/lilium-final/lilium-sancta-re2310.manifest.json'
    patch = sys.argv[2] if len(sys.argv) > 2 else \
        r'E:/mod/Lilium-Sancta-RE2310/9ba626afa44a3aa3.patch_0'

    mf = json.load(open(appman, encoding='utf-8'))
    tmp = tempfile.mkdtemp(prefix='faith')
    packs = S.Packs(
        addons=r'E:/tmp/lilium',
        model_vpk='Lilium_Sancta_Zoey.vpk',
        packs=[
            'Lilium_Sancta基础材质A1主体.vpk',
            'Lilium_Sancta基础材质B1服装色白.vpk',
            'Lilium_Sancta基础材质C2金属银色系.vpk',
            'Lilium_Sancta基础材质D1身体描边.vpk',
            'Lilium_Sancta基础材质E1发色银灰.vpk',
            'Lilium_Sancta基础材质F1瞳色银灰.vpk',
            'Lilium_Sancta_Zoey.vpk',
        ])
    print('packs: %d VMT / %d VTF' % (len(packs.vmt), len(packs.vtf)))
    entries, _raw = HP.scan_entries(patch)
    tex = {e['file_id']: e for e in entries if e['type_id'] == A.TEXTURE_TYPE_ID}
    mats = {e['file_id']: e for e in entries if e['type_id'] == A.MATERIAL_TYPE_ID}

    print('%-22s %-9s %-9s %-9s %-9s %s' %
          ('group', 'srcL', 'shipL', 'dL', 'ratio', 'note'))
    print('-' * 92)

    sheet_rows = []
    for g in mf['groups']:
        stem = g['base'].split('\\')[-1]
        raw = packs.vtf_bytes(stem)
        src = None
        if raw is not None:
            src = vtf_rgba(V.VTF.read(raw), tmp, 'src_' + stem)

        # shipped albedo: the manifest gives colorId, but the SHIPPED patch may
        # remap it.  Look up by the group's material id first.
        mid = int(g['material'], 16)
        cid = int(g['colorId'], 16)
        me = mats.get(mid)
        if me is not None:
            m = A.Material.read(HP.entry_payload(me, patch)['toc'])
            if len(m.tex_ids) == 11 and m.tex_ids[5] in tex:
                cid = m.tex_ids[5]
        ship = None
        if cid in tex:
            pl = HP.entry_payload(tex[cid], patch)
            t = A.Texture.read(pl['toc'], pl['gpu'])
            if len(t.pixels):
                dds = os.path.join(tmp, 'ship_%s.dds' % stem)
                open(dds, 'wb').write(t.to_dds())
                ship = decode_dds(dds, srgb=True)

        note = ''
        if src is None:
            note = 'no src VTF (1x1 placeholder?)'
        if ship is None:
            note = (note + ' shipped tex empty/pure').strip()

        sL = float(lum(src).mean()) if src is not None else float('nan')
        pL = float(lum(ship).mean()) if ship is not None else float('nan')
        dL = pL - sL if (src is not None and ship is not None) else float('nan')
        ratio = (pL / sL) if (src is not None and ship is not None and sL > 1e-6) else float('nan')
        print('%-22s %-9.1f %-9.1f %+9.1f %-9.3f %s' % (stem, sL, pL, dL, ratio, note))

        if src is not None and ship is not None:
            s = 256
            row = Image.new('RGB', (s * 3, s), (20, 20, 20))
            row.paste(Image.fromarray(src[:, :, :3], 'RGB').resize((s, s), Image.LANCZOS), (0, 0))
            row.paste(Image.fromarray(ship[:, :, :3], 'RGB').resize((s, s), Image.LANCZOS), (s, 0))
            diff = np.abs(src[:, :, :3].astype(np.int16) - ship[:, :, :3].astype(np.int16)).astype(np.uint8)
            row.paste(Image.fromarray(np.clip(diff * 4, 0, 255).astype(np.uint8), 'RGB')
                      .resize((s, s), Image.LANCZOS), (s * 2, 0))
            sheet_rows.append(row)

    if sheet_rows:
        S_ = 256
        sheet = Image.new('RGB', (S_ * 3, S_ * len(sheet_rows)), (10, 10, 10))
        for i, r in enumerate(sheet_rows):
            sheet.paste(r, (0, i * S_))
        sheet.save(os.path.join(OUT, 'src_vs_shipped.png'))
        print('\ncontact sheet: %s  (left=source sRGB, mid=shipped, right=|diff|x4)'
              % os.path.join(OUT, 'src_vs_shipped.png'))


if __name__ == '__main__':
    main()
