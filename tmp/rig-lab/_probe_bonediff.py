# -*- coding: utf-8 -*-
"""骨架名对齐预检：Lunalice_Zoey vs 8 个 Lilium 角色。

只读。回答一个问题：retarget() 的骨骼映射键（ValveBiped.* / SNAP_TOES）
在新模型里是否全部存在；若缺，缺哪些、是否在关键链路上。
"""
import os
import sys
import collections

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import hd2_style as ST

LUN = (r'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       r'Lunalice_Zoey.vpk')
LIL = r'E:/tmp/lilium'
LIL_ROLES = [
    ('survivor_teenangst', 'Lilium_Sancta_Zoey.vpk'),
    ('survivor_namvet',    'Lilium_Sancta_Bill.vpk'),
    ('survivor_biker',     'Lilium_Sancta_Francis.vpk'),
    ('survivor_manager',   'Lilium_Sancta_Louis.vpk'),
    ('survivor_gambler',   'Lilium_Sancta_Nick.vpk'),
    ('survivor_producer',  'Lilium_Sancta_Rochelle.vpk'),
    ('survivor_coach',     'Lilium_Sancta_Coach.vpk'),
    ('survivor_mechanic',  'Lilium_Sancta_Ellis.vpk'),
]


def names_of(vpk, model):
    _p, _m, verts, tris, src, short = ST.load_source(vpk, model)
    return [b['name'] for b in src], set(short), len(verts), len(tris)


def main():
    base_names, base_short, bv, bt = names_of(LUN, 'survivor_teenangst')
    base = set(base_names)
    print('Lunalice_Zoey : %d bones / %d verts / %d tris' % (len(base), bv, bt))
    print('  短名(SRC_PREFIX 去掉后) %d 个，示例: %s'
          % (len(base_short), sorted(base_short)[:6]))

    # retarget 用到的映射键（源侧短名）
    pairs = ST.snap_pairs('mesh') + ST.SNAP_TOES
    keys = sorted({sn for sn, _hn in pairs})
    print('retarget 映射键: %d 个' % len(keys))
    missing_base = [k for k in keys if k not in base_short]
    print('  在 Lunalice 中缺失: %s' % (missing_base or '无'))
    print()

    allroles = {}
    for model, f in LIL_ROLES:
        p = os.path.join(LIL, f)
        try:
            nms, sh, v, t = names_of(p, model)
        except Exception as ex:
            print('  !! %-22s %s: %s' % (model, type(ex).__name__, ex))
            continue
        s = set(nms)
        allroles[model] = s
        only_base = sorted(base - s)
        only_new = sorted(s - base)
        missing_keys = [k for k in keys if k not in sh]
        pref = collections.Counter(n.split('.')[0] for n in nms)
        print('%-22s bones=%-4d verts=%-7d tris=%-7d' % (model, len(nms), v, t))
        print('    retarget 键缺失 : %s' % (missing_keys or '无'))
        print('    仅 Lunalice 有   : %s' % (only_base or '无'))
        print('    仅本模型有       : %s' % (only_new or '无'))
        print('    前缀分布         : %s' % dict(pref))

    if allroles:
        common = set.intersection(*allroles.values())
        print()
        print('8 个角色共有的骨骼: %d' % len(common))
        print('8 个角色集合是否一致: %s'
              % ('是' if all(s == next(iter(allroles.values())) for s in allroles.values())
                 else '否'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
