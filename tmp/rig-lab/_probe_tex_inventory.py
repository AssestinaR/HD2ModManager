# -*- coding: utf-8 -*-
"""List every texture in the shipping patch: id, size, dxgi, and which
material slot it occupies, so we know exactly which ones are albedo (slot 5
of advanced / slot 2 of alphaclip) and therefore candidates for darkening.

usage: _probe_tex_inventory.py [patch_0]
"""
import os, sys, struct, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_asset as A
import hd2_patch as HP

DXGI = {98: 'BC7_UNORM', 99: 'BC7_UNORM_SRGB', 71: 'BC1_UNORM', 72: 'BC1_UNORM_SRGB',
        74: 'BC2_UNORM', 75: 'BC2_UNORM_SRGB', 77: 'BC3_UNORM', 78: 'BC3_UNORM_SRGB',
        83: 'BC5_UNORM', 80: 'BC4_UNORM', 28: 'R8G8B8A8_UNORM', 10: 'R16G16B16A16_FLOAT'}


def main():
    patch = sys.argv[1] if len(sys.argv) > 1 else \
        r'E:/mod/Lilium-Sancta-RE2310/9ba626afa44a3aa3.patch_0'
    entries, _raw = HP.scan_entries(patch)
    tex = {}
    for e in entries:
        if e['type_id'] != A.TEXTURE_TYPE_ID:
            continue
        pl = HP.entry_payload(e, patch)
        try:
            t = A.Texture.read(pl['toc'], pl['gpu'])
            hdr = bytes(t.dds_header)
            tex[e['file_id']] = dict(
                w=struct.unpack_from('<I', hdr, 16)[0],
                h=struct.unpack_from('<I', hdr, 12)[0],
                dxgi=struct.unpack_from('<I', hdr, 128)[0],
                px=len(t.pixels),
                gpu_off=pl.get('gpu_off'), toc_off=pl.get('toc_off'))
        except Exception as ex:
            tex[e['file_id']] = dict(err=str(ex))

    # map slot usage
    usage = {}
    for e in entries:
        if e['type_id'] != A.MATERIAL_TYPE_ID:
            continue
        m = A.Material.read(HP.entry_payload(e, patch)['toc'])
        n = len(m.tex_ids)
        for i, tid in enumerate(m.tex_ids):
            role = None
            if n == 11:
                role = {2: 'ADV::Normal/AO/Rough', 3: 'ADV::Metallic', 5: 'ADV::ALBEDO'}.get(i)
            elif n == 3:
                role = {0: 'AC::Normal', 1: 'AC::Mask', 2: 'AC::ALBEDO'}.get(i)
            if role:
                usage.setdefault(tid, []).append((hex(e['file_id']), role))
            else:
                usage.setdefault(tid, [])

    print('%-18s %-11s %-16s %-10s %s' % ('texId', 'size', 'dxgi', 'bytes', 'used as'))
    print('-' * 96)
    for tid, info in sorted(tex.items(), key=lambda kv: -kv[1].get('w', 0) * kv[1].get('h', 0)):
        if 'err' in info:
            print('0x%016x  <err %s>' % (tid, info['err'])); continue
        roles = usage.get(tid, [])
        rl = ','.join('%s@%s' % (r, mm) for mm, r in roles) if roles else '(unused/vanilla)'
        print('0x%016x  %4dx%-5d  %-16s %-10d %s'
              % (tid, info['w'], info['h'], DXGI.get(info['dxgi'], str(info['dxgi'])),
                 info['px'], rl))


if __name__ == '__main__':
    main()
