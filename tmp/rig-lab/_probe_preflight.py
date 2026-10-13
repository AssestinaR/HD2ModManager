# -*- coding: utf-8 -*-
"""通用预检：给定 L4D2 模型 VPK + 模型名，报告骨架/材质/几何是否与既有链路同构。只读。"""
import os, sys, collections
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE)
import export_obj as E
import hd2_style as ST

def preflight(vpk, model, label=''):
    print('=' * 96)
    print('%s  %s :: %s' % (label, os.path.basename(vpk), model))
    print('=' * 96)
    mdl_path, mdl, verts, tris, src_bones, short = ST.load_source(vpk, model)
    print('  mdl        : %s' % mdl_path)
    print('  verts      : %d    tris: %d' % (len(verts), len(tris)))
    print('  src bones  : %d    prefix-matched: %d' % (len(src_bones), len(short)))
    names = [b['name'] for b in src_bones]
    print('  前 8 个骨骼 : %s' % names[:8])
    # 骨骼名前缀分布
    pref = collections.Counter(n.split('.')[0] for n in names)
    print('  前缀分布   : %s' % dict(pref))
    # 关键骨骼存在性
    key = ['ValveBiped.Bip01_Head1', 'ValveBiped.Bip01_L_Hand', 'ValveBiped.Bip01_R_Hand',
           'ValveBiped.Bip01_Spine4', 'ValveBiped.Bip01_Pelvis']
    for k in key:
        print('    %-34s %s' % (k, 'OK' if k in names else '**缺失**'))
    # 材质槽
    _v, do, ents = E.parse_vpk_tree(vpk)
    mdls = [f for f, a, o, l in ents if f.lower().endswith('.mdl')]
    print('  vpk 内 mdl : %s' % mdls)
    return dict(verts=len(verts), tris=len(tris), bones=len(src_bones), names=names)

if __name__ == '__main__':
    LUN = r'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/Lunalice_Zoey.vpk'
    preflight(LUN, 'survivor_teenangst', 'Lunalice')
    print()
    for stem, f in [('survivor_teenangst', 'Lilium_Sancta_Zoey.vpk'),
                    ('survivor_namvet', 'Lilium_Sancta_Bill.vpk'),
                    ('survivor_biker', 'Lilium_Sancta_Francis.vpk'),
                    ('survivor_manager', 'Lilium_Sancta_Louis.vpk'),
                    ('survivor_gambler', 'Lilium_Sancta_Nick.vpk'),
                    ('survivor_producer', 'Lilium_Sancta_Rochelle.vpk'),
                    ('survivor_coach', 'Lilium_Sancta_Coach.vpk'),
                    ('survivor_mechanic', 'Lilium_Sancta_Ellis.vpk')]:
        try:
            preflight(os.path.join(r'E:/tmp/lilium', f), stem, 'Lilium')
        except Exception as ex:
            print('  !! %s :: %s -> %s: %s' % (f, stem, type(ex).__name__, ex))
        print()
