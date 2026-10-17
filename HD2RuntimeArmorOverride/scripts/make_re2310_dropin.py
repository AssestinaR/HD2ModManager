# -*- coding: utf-8 -*-
"""造一个"同键替换 RE-2310"的交付包：纯 patch，不依赖 Lua 运行时。

原理
----
HD2 的 patch 按 **AssetKey** 覆盖资产。所以"替换某件护甲"就是**把我们做的 Unit 写到
那件护甲的 Unit 键上**；它剩下的 Unit 用隐藏占位盖掉，否则原版几何会和我们的模型打架。

RE-2310 是一个**两件套**（setId 628829860）：

    护甲 itemId 820333399   23 个 Piece / 23 个 Unit / 3 行（body 3/0/1 = 7/8/8）
    头盔 itemId 1745464610   1 个 Piece / 1 个 Unit（af431352d9b95e83）

键的分类（全部来自 `Inspect-KitPieces.lua` / `Inspect-UnitSharing.lua` 的实测）：

  替换 11 个 —— 槽 2/3/6/7 的 type=1 各有 body=0 与 body=1 两份，必须**两个键都写同一份
                payload**，否则另一种体型（stocky/slim）的玩家会看到空槽；
                槽 4/5 只有 body=3 一份；头盔一个。
  隐藏 12 个 —— 每槽的 type=0 / type=2、以及 slot 8/9（MaterialLut/PatternLut）。
  不动  1 个 —— 3c33cf10a26cbb3e（body=3 slot=1 type=0，全库唯一零辅助字节的 piece），
                被 **101 个 Kit** 共用，碰它会让 101 件护甲一起变。

隐藏占位的来源
--------------
不自己造：`evidence/vanilla-hide-ab-20260928/patch/9ba626afa44a3aa3.patch_5` 是那次
"隐藏全部原版护甲 Unit"实验的产物，里面**按原键**有 1939 个正确占位，
我们要的 12 个全在。直接取原键的 payload，比重新推导更可靠。

用法：python make_re2310_dropin.py [--out <目录>]
"""
import argparse
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = r'E:\Data\source\repos\WpfApp1'
# 读/写 patch 的库在 tmp/rig-lab 下（那是 Python 实验线的工具，本脚本复用它们，
# 不另写一份 TOC/GPU 打包逻辑）。
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(REPO, 'tmp', 'rig-lab'))

import hd2_patch as HP
import hd2_patchw as PW
import hd2_unit as U

OURS = r'E:\mod\M3-verify-Unaligned\9ba626afa44a3aa3.patch_0'
HIDE = os.path.join(REPO, 'HD2RuntimeArmorOverride', 'evidence',
                    'vanilla-hide-ab-20260928', 'patch', '9ba626afa44a3aa3.patch_5')

# 我们的 payload（合并版七份本体）-> 目标键
REPLACE = {
    '46b0ed34887b0001': ['af431352d9b95e83'],                                  # Head -> RE-2310 头盔
    '46b0ed34887b0002': ['cae35ee15168145f', '9841b16544c8fbae'],              # Chest  body1/body0
    '46b0ed34887b0003': ['17da843a4908511e', '45d79d84b8933a7f'],              # Hips
    '46b0ed34887b0004': ['c9904983657e9fc8'],                                  # LeftLeg  body3
    '46b0ed34887b0005': ['9eb91a5581707ef1'],                                  # RightLeg body3
    '46b0ed34887b0006': ['91eb7be84016ceab', '9a5e6dc74cdb08a3'],              # LeftArm
    '46b0ed34887b0007': ['7336c6b662522f0e', '4f7bce4ca49ccda8'],              # RightArm
}

HIDE_KEYS = [
    '9a7b05af54f6722f', '226dfb2e738ae56e', 'b22e65e604a6ef18', '7cfd3805e70ea7bd',
    '914e7539788418fc', '5de0cf947d0e6520', '7b355519dcfb3085', '3a91f5a56da3201e',
    '3692b4d3a0b8d5d3', '3c09e08493ee44b8', '144d95c1500b73bc', '6533a416930570a5',
]

# 共用披风：只用于断言"没被碰"
SHARED_CAPE = '3c33cf10a26cbb3e'


def load(patch):
    entries, _ = HP.scan_entries(patch)
    out = {}
    for e in entries:
        if e['type_id'] != U.UNIT_ID:
            continue
        pl = HP.entry_payload(e, patch)
        out[e['file_id']] = (pl['toc'], pl['stream'], pl['gpu'], e)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default=r'E:\mod\Lunalice-RE2310-Release')
    args = ap.parse_args()

    ours = load(OURS)
    hide = load(HIDE)
    print('我方包 Unit %d 个；隐藏包 Unit %d 个' % (len(ours), len(hide)))

    missing = [k for k in REPLACE if int(k, 16) not in ours]
    if missing:
        raise SystemExit('我方包缺 Unit：%s' % missing)
    missing = [k for k in HIDE_KEYS if int(k, 16) not in hide]
    if missing:
        raise SystemExit('隐藏包缺占位：%s' % missing)
    if int(SHARED_CAPE, 16) in hide:
        print('  注意：共用披风在隐藏包里也有占位，本工具**不会**使用它')

    entries = []
    replaced_keys = []
    for source, targets in REPLACE.items():
        toc, stream, gpu, src = ours[int(source, 16)]
        for target in targets:
            entries.append(PW.Entry(U.UNIT_ID, int(target, 16), toc, stream, gpu,
                                    src['unknown1'], src['unknown2'], src['unknown3'],
                                    src['unknown4'], note='replace:%s->%s' % (source[-4:], target[-4:])))
            replaced_keys.append(target)
        print('  %-16s -> %s' % (source, ' '.join(targets)))

    for key in HIDE_KEYS:
        toc, stream, gpu, src = hide[int(key, 16)]
        entries.append(PW.Entry(U.UNIT_ID, int(key, 16), toc, stream, gpu,
                                src['unknown1'], src['unknown2'], src['unknown3'],
                                src['unknown4'], note='hide:%s' % key[-4:]))
    print('  隐藏 %d 个：%s' % (len(HIDE_KEYS), ' '.join(k[-4:] for k in HIDE_KEYS)))

    total = len(REPLACE) + len(HIDE_KEYS)
    print('合计 %d 个 Unit 条目（替换 %d 个键 + 隐藏 %d 个）' % (len(entries), len(replaced_keys), len(HIDE_KEYS)))
    if len(entries) != 23:
        raise SystemExit('条目数 %d != 23（RE-2310 的 23 个 Piece）' % len(entries))

    out = os.path.abspath(args.out)
    os.makedirs(out, exist_ok=True)
    result = PW.write_patch(entries, OURS, out, '9ba626afa44a3aa3.patch_0')
    print('写出 %s (%d B) + gpu (%d B)' % (result['toc'], result['toc_size'], result['gpu_size']))

    # 材质包原样搬过去（我们的 Unit 引用的材质在里面）
    material = os.path.join(os.path.dirname(OURS), '9ba626afa44a3aa3.patch_1')
    if os.path.exists(material):
        for suffix in ('', '.gpu_resources', '.stream'):
            src = material + suffix
            if os.path.exists(src):
                shutil.copy2(src, os.path.join(out, '9ba626afa44a3aa3.patch_1' + suffix))
        print('材质包已复制：9ba626afa44a3aa3.patch_1')

    # ---- 验证 ----
    back = load(result['toc'])
    if len(back) != 23:
        raise SystemExit('读回 %d 个 Unit，期望 23' % len(back))
    for source, targets in REPLACE.items():
        want = ours[int(source, 16)][0]
        for target in targets:
            got = back[int(target, 16)][0]
            if got != want:
                raise SystemExit('%s 的 payload 与 %s 不一致' % (target, source))
    for key in HIDE_KEYS:
        if back[int(key, 16)][0] != hide[int(key, 16)][0]:
            raise SystemExit('%s 的隐藏占位与隐藏包不一致' % key)
    if int(SHARED_CAPE, 16) in back:
        raise SystemExit('共用披风 %s 被写进了包 —— 绝不允许' % SHARED_CAPE)
    print('验证通过：23 个 Unit、替换键 payload 与源逐字节一致、隐藏占位一致、共用披风未出现')
    return 0


if __name__ == '__main__':
    sys.exit(main())
