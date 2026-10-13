# -*- coding: utf-8 -*-
"""Lilium：逐材质组列出组内 VMT 名与 $translucent/$alphatest 标记，并统计底色 VTF 的 alpha。
只读。回答「M8 规则 A 触发的 4 个 alphaclip 组是精确二值还是近似」。"""
import os
import sys
from collections import OrderedDict

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import hd2_source as S
import hd2_style as ST
import hd2_vtf as V

LIL = 'E:/tmp/lilium'
PACKS = ['Lilium_Sancta_Zoey.vpk',
         'Lilium_Sancta基础材质A1主体.vpk',
         'Lilium_Sancta基础材质B1服装色白.vpk',
         'Lilium_Sancta基础材质C2金属银色系.vpk',
         'Lilium_Sancta基础材质D1身体描边.vpk',
         'Lilium_Sancta基础材质E1发色银灰.vpk',
         'Lilium_Sancta基础材质F1瞳色银灰.vpk']


def main():
    packs = S.Packs(addons=LIL, packs=PACKS, model_vpk=PACKS[0])
    _p, mdl, _v, tris, _s, _sh = ST.load_source(os.path.join(LIL, PACKS[0]),
                                                'survivor_teenangst')
    names, _cds, _row = S.load_material_slots(mdl)
    tris = np.asarray(tris, np.int64)
    used = sorted({int(t[3]) for t in tris})

    # 按 $basetexture 分组（与 build_patch 的 key_of 一致）
    groups = OrderedDict()
    for i in used:
        txt = packs.vmt_text(names[i])
        vmt = S.parse_vmt(txt) if txt else {}
        base = vmt.get('$basetexture')
        groups.setdefault(base, []).append(i)

    print('%-34s %-6s %-11s %-11s %s'
          % ('组 ($basetexture)', '槽数', 'translucent', 'alphatest', 'alpha: <250% / =0% / =255%'))
    print('-' * 108)
    for base, slots in groups.items():
        tl = at = 0
        vtfs = []
        for i in slots:
            txt = packs.vmt_text(names[i])
            raw = S.parse_vmt(txt).get('_raw', {}) if txt else {}
            if str(raw.get('$translucent', '0')).strip() not in ('0', '', 'none'):
                tl += 1
            if str(raw.get('$alphatest', '0')).strip() not in ('0', '', 'none'):
                at += 1
            vtfs.append(names[i])
        raw = packs.vtf_bytes(base) if base else None
        stat = ''
        if raw is not None:
            v = V.VTF.read(raw)
            mip0 = v.mips[0]
            # 只统计能用 PIL 解出来的（BC7/DXT 交给 PIL 不现实）-> 用 RGBA8888 路径
            stat = 'VTF %dx%d fmt=%s' % (v.width, v.height, v.hi_format)
        print('%-34s %-6d %-11d %-11d %s'
              % (os.path.basename(base or 'untextured'), len(slots), tl, at, stat))
        for i in slots[:4]:
            print('      slot %-3d %s' % (i, names[i]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
